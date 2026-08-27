using System.Diagnostics;
using System.Globalization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Open;

/// Windows'un yerel konuşma sentezi (WinRT) üzerinden TTS — anahtarsız.
///
/// ADR-015'in ses ayağı. Bu makinede `Microsoft Tolga` (tr-TR) kurulu;
/// ücretli bir TTS'e geçmeden gerçek Türkçe seslendirme üretilebiliyor.
///
/// Neden PowerShell üzerinden: WinRT API'lerine doğrudan erişmek projeyi
/// `net10.0-windows` hedefine kilitlerdi ve Linux CI derleyemezdi. Alt süreç
/// çağrısı, Windows bağımlılığını ÇALIŞMA ZAMANINA hapsediyor — kod her
/// yerde derleniyor, yalnızca burada çalışıyor.
///
/// Kalite sınırı biliniyor: SAPI/WinRT sesleri ElevenLabs seviyesinde değil
/// ve kelime zamanlaması vermiyor. Bu yüzden `SupportsWordTimings` false;
/// altyazı için ASR yan servisine düşülüyor (§12.7).
public sealed class WindowsSpeechTtsProvider(string? powershellPath = null) : ITtsProvider
{
    private readonly string _powershell = powershellPath ?? "powershell";

    public string Key => "windows-speech";

    /// WinRT sentezleyici kelime zamanı döndürmüyor; ASR yedeği devreye girer.
    public bool SupportsWordTimings => false;

    public async Task<Result<ProviderResponse<TtsResponse>>> SynthesizeAsync(
        TtsRequest request, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OperatingSystem.IsWindows())
        {
            return Error.Permanent("windows_speech.unsupported",
                "Bu sağlayıcı yalnızca Windows'ta çalışır.");
        }

        if (string.IsNullOrWhiteSpace(request.SpeechText))
        {
            return Error.Permanent("windows_speech.empty", "Seslendirilecek metin boş.");
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(), $"bmai-tts-{Guid.CreateVersion7():N}.wav");

        try
        {
            var script = BuildScript(request, outputPath);
            var run = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);

            if (run.IsFailure)
            {
                return Result.Failure<ProviderResponse<TtsResponse>>(run.Error);
            }

            // DİL İÇİN SES YOKSA ÜRETİM YAPILMIYOR.
            //
            // KAYNAK hatası, başarısızlık değil (ADR-011): eksik olan
            // şey bir dil paketi ve kurulduğunda aynı iş çalışacak.
            // Sessizce varsayılan sese düşmek, İngilizce metni Türkçe
            // sesle okutup videoyu yayına vermek demekti.
            if (run.Value.StartsWith("NOVOICE", StringComparison.Ordinal))
            {
                return Error.Resource("windows_speech.no_voice",
                    $"'{request.Language.Value}' için kurulu ses yok ({run.Value[8..].Trim()}). "
                    + "Windows Ayarlar > Saat ve Dil > Dil ve bölge > dil ekleyin ve "
                    + "'Konuşma' özelliğini işaretleyin.",
                    TimeSpan.FromHours(1));
            }

            if (!File.Exists(outputPath))
            {
                return Error.Transient("windows_speech.no_output",
                    $"Ses dosyası üretilmedi. Çıktı: {run.Value}");
            }

            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);

            if (bytes.Length < 1024)
            {
                return Error.Transient("windows_speech.too_small",
                    $"Ses dosyası çok küçük ({bytes.Length} bayt).");
            }

            return Result.Success(new ProviderResponse<TtsResponse>(
                new TtsResponse
                {
                    Audio = bytes,
                    MimeType = "audio/wav",
                    // Bildirilen süre başlıktan hesaplanıyor ama OTORİTE DEĞİL:
                    // timeline'a giren süre her zaman ffprobe ile ölçülenidir
                    // (ADR-006).
                    ReportedDuration = EstimateDuration(bytes),
                    WordTimings = [],
                    // Betik "OK <bayt> | <ses> (<dil>)" yaziyor.
                    VoiceUsed = VoiceFrom(run.Value),
                },
                new UsageUnits { Characters = request.SpeechText.Length }));
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    public Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesAsync(
        LanguageTag language, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(Result.Success<IReadOnlyList<VoiceInfo>>([]));
        }

        return ListVoicesCoreAsync(language, cancellationToken);
    }

    private async Task<Result<IReadOnlyList<VoiceInfo>>> ListVoicesCoreAsync(
        LanguageTag language, CancellationToken cancellationToken)
    {
        const string script = """
            $null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media, ContentType=WindowsRuntime]
            [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices |
                ForEach-Object { "$($_.Id)|$($_.DisplayName)|$($_.Language)|$($_.Gender)" }
            """;

        var run = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);

        if (run.IsFailure)
        {
            return Result.Failure<IReadOnlyList<VoiceInfo>>(run.Error);
        }

        var voices = new List<VoiceInfo>();

        foreach (var line in run.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('|');

            if (parts.Length < 4)
            {
                continue;
            }

            var tag = LanguageTag.TryCreate(parts[2]);
            if (tag.IsFailure)
            {
                continue;
            }

            // Dil eşleşmesi ana etiket üzerinden: "tr-TR" isteyip "tr" bulmak
            // ya da tersi normal.
            if (!string.Equals(tag.Value.Primary, language.Primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            voices.Add(new VoiceInfo
            {
                VoiceId = parts[0],
                DisplayName = parts[1],
                Language = tag.Value,
                Gender = parts[3],
            });
        }

        return Result.Success<IReadOnlyList<VoiceInfo>>(voices);
    }

    /// Betiğin çıktısından GERÇEKTEN kullanılan sesi okur.
    ///
    /// İstenen ses ile kullanılan ses aynı olmayabiliyor; fark burada
    /// görünür hâle geliyor ve node çıktısına yazılıyor.
    internal static string? VoiceFrom(string output)
    {
        var separator = output.IndexOf('|', StringComparison.Ordinal);

        return separator >= 0 && separator + 1 < output.Length
            ? output[(separator + 1)..].Trim()
            : null;
    }

    /// Sentez betiği.
    ///
    /// Metin base64 ile geçiriliyor: tırnak, satır sonu ve `$` gibi
    /// karakterlerin PowerShell tarafından yorumlanması engelleniyor.
    /// Doğrudan gömülseydi senaryodaki bir tırnak betiği bozar, en kötü
    /// ihtimalle komut enjeksiyonuna açık hâle getirirdi.
    private static string BuildScript(TtsRequest request, string outputPath)
    {
        var encodedText = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(request.SpeechText));

        var encodedPath = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(outputPath));

        var encodedVoiceId = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(request.VoiceId ?? string.Empty));

        // Konuşma hızı SSML ile veriliyor: WinRT'nin doğrudan hız ayarı yok.
        var ratePercent = (int)Math.Round((request.Speed - 1.0) * 100);

        return $$"""
            $ErrorActionPreference = 'Stop'
            $text = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedText}}'))
            $out  = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedPath}}'))

            $null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media, ContentType=WindowsRuntime]
            $null = [Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType=WindowsRuntime]
            Add-Type -AssemblyName System.Runtime.WindowsRuntime

            $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

            function Await($op, $type) {
                $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
                $t.Wait(-1) | Out-Null
                $t.Result
            }

            $syn = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
            $all = [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices

            # ONCE ISTENEN SES, sonra dile gore.
            #
            # Istenen ses kimligi baska bir saglayiciya ait olabiliyor
            # (hattin varsayilani "fake-tr-f1"); o zaman eslesmiyor ve
            # dile gore seciliyor. Ama DIL eslesmezse secim yapilmiyor:
            # varsayilan sese dusmek, Ingilizce metni Turkce sesle
            # okutmak demekti ve bu hicbir yerde gorunmezdi.
            $wanted = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedVoiceId}}'))
            $voice = $null

            if ($wanted) {
                $voice = $all | Where-Object { $_.Id -eq $wanted -or $_.DisplayName -eq $wanted } | Select-Object -First 1
            }

            if (-not $voice) {
                $voice = $all | Where-Object { $_.Language -like '{{request.Language.Primary}}*' } | Select-Object -First 1
            }

            if (-not $voice) {
                $kurulu = ($all | ForEach-Object { $_.Language }) -join ', '
                "NOVOICE {{request.Language.Value}} | kurulu: $kurulu"
                exit 0
            }

            $syn.Voice = $voice

            $ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{{request.Language.Value}}'>" +
                    "<prosody rate='{{(ratePercent >= 0 ? "+" : string.Empty)}}{{ratePercent.ToString(CultureInfo.InvariantCulture)}}%'>" +
                    [System.Security.SecurityElement]::Escape($text) + "</prosody></speak>"

            $stream = Await ($syn.SynthesizeSsmlToStreamAsync($ssml)) ([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])
            $reader = New-Object Windows.Storage.Streams.DataReader($stream.GetInputStreamAt(0))
            $null = Await ($reader.LoadAsync([uint32]$stream.Size)) ([uint32])
            $bytes = New-Object byte[] $stream.Size
            $reader.ReadBytes($bytes)
            [System.IO.File]::WriteAllBytes($out, $bytes)
            "OK $($bytes.Length) | $($voice.DisplayName) ($($voice.Language))"
            """;
    }

    private async Task<Result<string>> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var info = new ProcessStartInfo
        {
            FileName = _powershell,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // `-EncodedCommand`: betik tırnak ve kaçış sorunu olmadan geçiyor.
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(encoded);

        using var process = new Process { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Error.Permanent("windows_speech.no_powershell",
                $"PowerShell çalıştırılamadı: {ex.Message}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode == 0
            ? Result.Success(stdout)
            : Error.Transient("windows_speech.failed",
                $"Sentez {process.ExitCode} koduyla bitti: {stderr.Trim()}");
    }

    /// WAV başlığından süre. Yalnızca bilgi amaçlı — otorite ffprobe.
    private static Ms EstimateDuration(byte[] wav)
    {
        if (wav.Length < 44)
        {
            return Ms.Zero;
        }

        var byteRate = BitConverter.ToInt32(wav, 28);
        var dataSize = wav.Length - 44;

        return byteRate <= 0 ? Ms.Zero : new Ms((int)((long)dataSize * 1000 / byteRate));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Geçici dosya silinemezse sonucu etkilemez.
        }
    }
}
