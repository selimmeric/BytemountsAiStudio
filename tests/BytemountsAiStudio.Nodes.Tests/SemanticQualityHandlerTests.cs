using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes.Tests;

/// Semantik QC node'u (P2-06).
///
/// GÖRME MODELİ HİÇ YÜKLENMİYOR. Yargılar sahte bir sağlayıcıdan
/// geliyor ve bu bir kolaylık değil zorunluluk: ana makinenin ekran
/// kartı model yüklenince sistemi çökertiyor. Sağlayıcı arayüzünün
/// arkasında durduğu için yerine dışarıdan bir API de takılabilir.
public sealed class SemanticQualityHandlerTests
{
    private sealed class StubVision(double relevance, Error? error = null) : IVisionProvider
    {
        public string Key => "stub-vision";

        public int Calls { get; private set; }

        public Task<Result<ProviderResponse<VisionVerdict>>> JudgeAsync(
            VisionQuery query, ProviderContext context, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(error is not null
                ? Result.Failure<ProviderResponse<VisionVerdict>>(error)
                : Result.Success(new ProviderResponse<VisionVerdict>(
                    new VisionVerdict { Relevance = relevance, Reason = "sahte" },
                    UsageUnits.OfRequests())));
        }
    }

    private static async Task<(JsonElement Output, FakeStorageProvider Storage)> RunAsync(
        IVisionProvider? vision, int sceneCount = 3, ILlmProvider? judge = null)
    {
        var storage = new FakeStorageProvider();

        var image = await storage.PutAsync(
            new MemoryStream([1, 2, 3]),
            new AssetMetadata { Kind = AssetKind.Image, MimeType = "image/png" },
            CancellationToken.None);

        var scenes = new List<Scene>();
        var segments = new List<VoiceSegment>();

        for (var i = 0; i < sceneCount; i++)
        {
            segments.Add(new VoiceSegment
            {
                Id = $"s{i}",
                Asset = image.Value.Ref,
                Start = new Ms(i * 3000),
                Duration = new Ms(3000),
                SpeechText = $"Cumle {i}.",
            });

            scenes.Add(new Scene
            {
                Index = i,
                Range = new TimeRange(new Ms(i * 3000), new Ms((i + 1) * 3000)),
                VoiceSegmentIds = [$"s{i}"],
                Visual = new SceneVisual { Asset = image.Value.Ref },
            });
        }

        var timeline = new TimelineDocument
        {
            Language = LanguageTag.Create("tr-TR"),
            Duration = new Ms(sceneCount * 3000),
            Canvas = Canvas.Shorts1080,
            Audio = new AudioTrack { VoiceSegments = segments },
            Scenes = scenes,
            Output = new OutputSpec { Preset = "shorts-1080x1920" },
        };

        var stored = await storage.PutAsync(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(TimelineJson.Serialize(timeline))),
            new AssetMetadata { Kind = AssetKind.Output, MimeType = "application/json" },
            CancellationToken.None);

        var handler = new SemanticQualityHandler(storage, vision, judge);

        var context = new NodeContext
        {
            RunId = Guid.CreateVersion7(),
            NodeId = "qcs",
            NodeType = "qc.semantic",
            Attempt = 1,
            Config = JsonDocument.Parse("{}").RootElement.Clone(),
            RunContext = JsonDocument.Parse(
                "{\"timeline\":{\"timeline_asset\":\"" + stored.Value.Ref + "\"}}").RootElement.Clone(),
            IdempotencyKey = "test",
            CorrelationId = "test",
        };

        var result = await handler.ExecuteAsync(context, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return (result.Value, storage);
    }

    /// MODEL YOKKEN NODE KOŞUYOR AMA GEÇMİYOR.
    ///
    /// Node'u düşürmek bir videoyu tamamen kaybetmek olurdu; sessizce
    /// geçmek ise kalite kontrolünün hiç koşmadığı bir sistemde her
    /// videoya tam puan vermekti. Doğru davranış: koş, düş, insana
    /// gönder.
    [Fact]
    public async Task ModelYok_KosuyorAmaSkorDusuk()
    {
        var (output, _) = await RunAsync(vision: null);

        Assert.False(output.GetProperty("vision_available").GetBoolean());
        Assert.False(output.GetProperty("judge_available").GetBoolean());

        // Skor düşük: onay kapısı bunu insana gönderecek.
        Assert.True(output.GetProperty("score").GetDouble() < 0.5,
            $"skor {output.GetProperty("score").GetDouble()}");

        // Hiçbir sahne ÖLÇÜLMEDİ olarak işaretli.
        foreach (var scene in output.GetProperty("scenes").EnumerateArray())
        {
            Assert.False(scene.GetProperty("measured").GetBoolean());
        }
    }

    [Fact]
    public async Task AlakaliGorseller_Geciyor()
    {
        var (output, _) = await RunAsync(new StubVision(0.9));

        Assert.True(output.GetProperty("vision_available").GetBoolean());

        var relevance = output.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "qc.visual_relevance");

        Assert.True(relevance.GetProperty("passed").GetBoolean());
    }

    /// KABUL KRİTERİ: alakasız görsel yerleştirilen video yakalanıyor.
    [Fact]
    public async Task AlakasizGorseller_Yakalaniyor()
    {
        var (output, _) = await RunAsync(new StubVision(0.1));

        var relevance = output.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("code").GetString() == "qc.visual_relevance");

        Assert.False(relevance.GetProperty("passed").GetBoolean());
        Assert.Equal("Visuals", relevance.GetProperty("target").GetString());
    }

    /// KAÇ SAHNENİN ÖLÇÜLDÜĞÜ YAZILIYOR: örnekleme yapıldığı için
    /// "hepsi kontrol edildi" izlenimi doğmamalı.
    [Fact]
    public async Task CokSahne_OrnekleniyorVeSayisiYaziliyor()
    {
        var (output, _) = await RunAsync(new StubVision(0.9), sceneCount: 20);

        var sampled = output.GetProperty("sampled_scenes").GetArrayLength();

        Assert.Equal(20, output.GetProperty("total_scenes").GetInt32());
        Assert.True(sampled <= 6, $"orneklenen {sampled}");
        Assert.True(sampled > 0);
    }

    /// MODEL ART ARDA DÜŞÜYORSA KALAN SAHNELER DENENMİYOR.
    ///
    /// Aynı hatayı sahne sayısı kadar tekrar etmek, her biri bir çağrı
    /// süresi harcayan yirmi denemeydi.
    [Fact]
    public async Task ModelKapali_TekSeferDeneniyor()
    {
        var stub = new StubVision(0, Error.Resource("vision.down", "kapali", TimeSpan.FromMinutes(5)));

        var (output, _) = await RunAsync(stub, sceneCount: 6);

        Assert.Equal(1, stub.Calls);

        // Yine de HER sahne için bir kayıt var ve hepsi ölçülemedi.
        foreach (var scene in output.GetProperty("scenes").EnumerateArray())
        {
            Assert.False(scene.GetProperty("measured").GetBoolean());
        }
    }

    /// Geçici bir hata tek sahneyi düşürüyor, diğerleri denenmeye
    /// devam ediyor: anlık bir zaman aşımı bütün ölçümü çöpe atmamalı.
    [Fact]
    public async Task GeciciHata_DigerSahneleriDurdurmuyor()
    {
        var stub = new StubVision(0, Error.Transient("vision.timeout", "zaman asimi"));

        await RunAsync(stub, sceneCount: 4);

        Assert.Equal(4, stub.Calls);
    }

    /// Timeline yoksa node DÜŞÜYOR: semantik QC timeline olmadan
    /// hiçbir şey ölçemez ve "ölçemedim" demek yerine sessizce boş
    /// geçmek yanlış olurdu.
    [Fact]
    public async Task TimelineYok_NodeDusuyor()
    {
        var handler = new SemanticQualityHandler(new FakeStorageProvider());

        var result = await handler.ExecuteAsync(
            new NodeContext
            {
                RunId = Guid.CreateVersion7(),
                NodeId = "qcs",
                NodeType = "qc.semantic",
                Attempt = 1,
                Config = JsonDocument.Parse("{}").RootElement.Clone(),
                RunContext = JsonDocument.Parse("{}").RootElement.Clone(),
                IdempotencyKey = "test",
                CorrelationId = "test",
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("qc.no_timeline", result.Error.Code);
    }

    /// EKSİK ALAN `null` KALIYOR, `false` DEĞİL: cevaplanmamış bir
    /// soru "hayır" değil "bilmiyoruz" demek ve ikisi farklı kontrol
    /// sonucu üretiyor.
    [Fact]
    public void EksikYargi_OlculemediSayiliyor()
    {
        var judgement = SemanticQualityHandler.ParseJudgement(
            """{"title_matches_content":true}""");

        Assert.True(judgement.TitleMatchesContent);
        Assert.Null(judgement.ToneAppropriate);
        Assert.Null(judgement.PolicySafe);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("json degil")]
    public void BozukYargi_HepsiOlculemedi(string? json)
    {
        var judgement = SemanticQualityHandler.ParseJudgement(json);

        Assert.Null(judgement.TitleMatchesContent);
        Assert.Null(judgement.ToneAppropriate);
        Assert.Null(judgement.PolicySafe);
        Assert.NotNull(judgement.Rationale);
    }

    [Fact]
    public void TamYargi_Okunuyor()
    {
        var judgement = SemanticQualityHandler.ParseJudgement(
            """{"title_matches_content":false,"tone_appropriate":true,"policy_safe":false,"rationale":"abartili baslik"}""");

        Assert.False(judgement.TitleMatchesContent);
        Assert.True(judgement.ToneAppropriate);
        Assert.False(judgement.PolicySafe);
        Assert.Equal("abartili baslik", judgement.Rationale);
    }
}
