<#
.SYNOPSIS
    Docker'siz yerel PostgreSQL 16 + pgvector.

.DESCRIPTION
    NEDEN VAR: Docker Desktop bu makinede acilmiyor - `%LOCALAPPDATA%\Docker\run`
    altindaki dort AF_UNIX soket dosyasi sahipsiz kalmis, silinemiyor
    (ReparsePoint; ne Remove-Item ne yeniden adlandirma ne `wsl --shutdown`
    ise yariyor) ve Docker acilista tam onlari silmeye calistigi icin
    cokuyor. Cozumu yeniden baslatma; ama veritabani gerektiren isin bir
    yeniden baslatmayi beklemesi gerekmiyor.

    NEDEN SQLite DEGIL: semanin degerli kismi SQLite'ta ifade EDILEMIYOR -
    `FOR UPDATE SKIP LOCKED` (kuyruk ve konu havuzu), pgvector benzerligi
    (ADR-003), JSONB, kismi indeksler. Ikinci bir sema, gecen ama hicbir
    sey kanitlamayan testler uretirdi. CI zaten gercek Postgres kosuyor;
    yerelde de ayni sey kossun.

    NE YAPIYOR: EDB'nin KURULUMSUZ ikili paketini kullaniciya ait bir
    klasore aciyor, veritabanini baslatiyor ve pgvector'u MSVC ile
    derleyip kuruyor. Yonetici yetkisi GEREKMIYOR, sisteme hicbir sey
    kurulmuyor: her sey tek bir klasorde ve silmek yetiyor.

    Baglanti dizesi DEGISMIYOR - varsayilanla ayni:
      Host=localhost;Port=5432;Database=bmai;Username=bmai;Password=bmai_dev

.PARAMETER Action
    setup   : indir, baslat, pgvector kur (varsayilan; tekrar calistirilabilir)
    start   : sunucuyu baslat
    stop    : sunucuyu durdur
    status  : durum
    remove  : her seyi sil

.EXAMPLE
    pwsh -File scripts/yerel-postgres.ps1
    pwsh -File scripts/yerel-postgres.ps1 -Action stop
#>
[CmdletBinding()]
param(
    [ValidateSet('setup', 'start', 'stop', 'status', 'remove')]
    [string]$Action = 'setup',

    # 5432 varsayilan cunku baglanti dizesi oyle. Docker geri gelirse
    # ikisi ayni portu isteyecek - o zaman birini durdurmak gerekiyor.
    [int]$Port = 5432,

    [string]$Version = '16.10-1'
)

$ErrorActionPreference = 'Stop'

$Root = Join-Path $env:LOCALAPPDATA 'bmai-postgres'
$PgRoot = Join-Path $Root 'pgsql'
$DataDir = Join-Path $Root 'data'
$LogFile = Join-Path $Root 'postgres.log'
$Bin = Join-Path $PgRoot 'bin'

$User = 'bmai'
$Password = 'bmai_dev'
$Database = 'bmai'

function Write-Step([string]$Text) { Write-Host "  $Text" }

function Test-Running {
    if (-not (Test-Path (Join-Path $Bin 'pg_ctl.exe'))) { return $false }
    & (Join-Path $Bin 'pg_ctl.exe') -D $DataDir status *>$null
    return $LASTEXITCODE -eq 0
}

# SQL bir DOSYADAN veriliyor, `-c` ile degil.
#
# Iki sebep, ikisi de yasandi:
#   - PowerShell yerel bir komuta gecirdigi argumanlari yeniden
#     tirnakliyor ve icerideki cift tirnaklar kayboluyor:
#     `CREATE EXTENSION "uuid-ossp"` -> tirnaksiz -> "syntax error at
#     or near -".
#   - Windows PowerShell 5.1'de `2>&1` her stderr satirini hataya
#     ceviriyor ve $ErrorActionPreference='Stop' sureci durduruyor.
function Invoke-Psql([string]$Db, [string]$Sql) {
    $file = Join-Path $Root 'komut.sql'

    # NOTICE'lar susturuluyor: "extension already exists, skipping"
    # gibi bilgi mesajlari PowerShell'de KIRMIZI hata gibi gorunuyor ve
    # basarili bir kurulumu basarisiz gosteriyor.
    Set-Content -Path $file -Value "SET client_min_messages = warning;`n$Sql" -Encoding utf8

    $env:PGPASSWORD = $Password

    try {
        $out = & (Join-Path $Bin 'psql.exe') -h 127.0.0.1 -p $Port -U $User -d $Db `
            -v ON_ERROR_STOP=1 -X -q -t -A -f $file
        $code = $LASTEXITCODE
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item $file -Force -ErrorAction SilentlyContinue
    }

    if ($code -ne 0) { throw "psql basarisiz ($Db): $out" }

    return $out
}

function Start-Server {
    if (Test-Running) { Write-Step 'Sunucu zaten calisiyor.'; return }

    # SADECE 127.0.0.1: varsayilan olsaydi bu veritabani yerel aga
    # acilirdi ve parola zaten gelistirme parolasi.
    & (Join-Path $Bin 'pg_ctl.exe') -D $DataDir -l $LogFile `
        -o "-p $Port -c listen_addresses=127.0.0.1" -w start | Out-Null

    if ($LASTEXITCODE -ne 0) { throw "Sunucu baslatilamadi. Log: $LogFile" }

    Write-Step "Sunucu calisiyor (port $Port)."
}

function Stop-Server {
    if (-not (Test-Running)) { Write-Step 'Sunucu zaten durmus.'; return }
    & (Join-Path $Bin 'pg_ctl.exe') -D $DataDir -m fast -w stop | Out-Null
    Write-Step 'Sunucu durduruldu.'
}

function Install-Binaries {
    if (Test-Path (Join-Path $Bin 'postgres.exe')) {
        Write-Step 'Ikili paket zaten var.'
        return
    }

    $zip = Join-Path $Root "postgresql-$Version-windows-x64-binaries.zip"

    if (-not (Test-Path $zip)) {
        $url = "https://get.enterprisedb.com/postgresql/postgresql-$Version-windows-x64-binaries.zip"
        Write-Step "Indiriliyor (~320 MB): $url"
        New-Item -ItemType Directory -Force -Path $Root | Out-Null

        # Invoke-WebRequest'in ilerleme cubugu buyuk dosyalarda cok
        # yavaslatiyor; kapatiliyor.
        $previous = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing }
        finally { $ProgressPreference = $previous }
    }

    Write-Step 'Aciliyor...'

    # SADECE GEREKEN KLASORLER aciliyor.
    #
    # Arsivin buyuk kismi pgAdmin 4 ve StackBuilder - grafik arayuz ve
    # kurulum yardimcisi, ikisi de burada gereksiz. Tamamini acmak
    # ~1,1 GB ve dakikalar; bize gereken ~250 MB.
    #
    # `include` LISTEDE cunku pgvector derlemesi basliklari istiyor;
    # atlanirsa hata ancak derleme adiminda cikardi.
    $wanted = @('pgsql/bin/', 'pgsql/lib/', 'pgsql/share/', 'pgsql/include/')

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)

    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }

            $keep = $false
            foreach ($prefix in $wanted) {
                if ($entry.FullName.StartsWith($prefix, 'OrdinalIgnoreCase')) { $keep = $true; break }
            }
            if (-not $keep) { continue }

            $target = Join-Path $Root ($entry.FullName -replace '/', '\')
            $folder = Split-Path $target -Parent
            if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }

            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Initialize-Cluster {
    if (Test-Path (Join-Path $DataDir 'PG_VERSION')) {
        Write-Step 'Veri klasoru zaten hazir.'
        return
    }

    # Parola DOSYADAN veriliyor: komut satirina yazmak, onu surec
    # listesine ve kabuk gecmisine dusurmek demekti.
    $passwordFile = Join-Path $Root 'initpw.txt'
    Set-Content -Path $passwordFile -Value $Password -NoNewline -Encoding ascii

    try {
        # Kultur bagimsiz siralama: docker-compose ile AYNI. Turkce ve
        # Ingilizce icerik ayni veritabaninda; dil degisince indeks
        # davranisi degismemeli.
        #
        # `--locale=C` ZORUNLU ve sebebi Turkce Windows: sistem yerelinin
        # adi `Turkish_Türkiye.1254` ve icindeki `ü` yuzunden initdb
        # "locale name contains non-ASCII characters" deyip reddediyor.
        # ICU saglayicisi siralamayi zaten devraliyor, libc yereli
        # yalnizca mesajlar icin kaliyor.
        #
        # ICU yereli `und` (kok), `und-x-icu` DEGIL: ikincisi
        # PostgreSQL'in ic siralama ADI, ICU yerel kimligi degil.
        # docker-compose'da o hali kabul ediliyor, buradaki ICU
        # surumu "unknown language x-icu" diyerek reddediyor. Ikisi de
        # ayni sonuca varıyor: kultur bagimsiz siralama.
        & (Join-Path $Bin 'initdb.exe') -D $DataDir -U $User --pwfile=$passwordFile `
            --encoding=UTF8 --locale=C --locale-provider=icu --icu-locale=und `
            --auth-host=scram-sha-256 | Out-Null

        if ($LASTEXITCODE -ne 0) { throw 'initdb basarisiz.' }
    }
    finally {
        Remove-Item $passwordFile -Force -ErrorAction SilentlyContinue
    }

    Write-Step 'Veri klasoru olusturuldu.'
}

function Install-Pgvector {
    $control = Join-Path $PgRoot 'share\extension\vector.control'

    if (Test-Path $control) {
        Write-Step 'pgvector zaten kurulu.'
        return
    }

    # Once VsDevCmd, sonra vcvars64: ilki desteklenen giris noktasi,
    # ikincisi eski surumlerde calisan yol.
    $entry = @(
        (Get-ChildItem 'C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio' `
            -Recurse -Filter 'VsDevCmd.bat' -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName),
        (Get-ChildItem 'C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio' `
            -Recurse -Filter 'vcvars64.bat' -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName)
    ) | Where-Object { $_ } | Select-Object -First 1

    if (-not $entry) {
        Write-PgvectorMissing 'MSVC bulunamadi.'
        return
    }

    $arguments = if ($entry -like '*VsDevCmd.bat') { ' -arch=amd64 -host_arch=amd64' } else { '' }

    $source = Join-Path $Root 'pgvector'

    if (-not (Test-Path $source)) {
        Write-Step 'pgvector kaynak kodu aliniyor...'
        git clone --quiet --branch v0.8.0 --depth 1 https://github.com/pgvector/pgvector.git $source
        if ($LASTEXITCODE -ne 0) { throw 'pgvector klonlanamadi.' }
    }

    Write-Step 'pgvector derleniyor (MSVC)...'

    # Komutlar GECICI BIR .BAT DOSYASINA yaziliyor, `cmd /c "..."`
    # olarak degil.
    #
    # Sebep: PowerShell yerel bir komuta gecirdigi argumanlari yeniden
    # tirnakliyor ve cmd bu tirnaklamayi anlamiyor - vcvars64.bat kendi
    # icinde vcvarsall.bat'i cagirdiginda yol parcalaniyor ve
    # "'...vcvarsall.bat' is not recognized" hatasi cikiyor. Dosyaya
    # yazmak tirnak sorununu tamamen ortadan kaldiriyor.
    #
    # nmake, vcvars64'un kurdugu ortami istiyor; ucu de AYNI oturumda
    # zincirlenmek zorunda.
    $script = Join-Path $Root 'build-pgvector.bat'
    $buildLog = Join-Path $Root 'build-pgvector.log'

    # Cikti .BAT ICINDE dosyaya yaziliyor, PowerShell tarafinda `2>&1`
    # ile DEGIL.
    #
    # Windows PowerShell 5.1'de yerel bir komutun stderr'ini `2>&1` ile
    # almak, her satiri hataya cevirip $ErrorActionPreference='Stop'
    # ile sureci durduruyor - derleme basarisiz oldugunda uyarip devam
    # etmesi gereken betik, cokuyordu.
    @(
        '@echo off'
        "call `"$entry`"$arguments"
        "set `"PGROOT=$PgRoot`""
        "cd /d `"$source`" || exit /b 1"
        'nmake /F Makefile.win || exit /b 1'
        'nmake /F Makefile.win install || exit /b 1'
    ) | Set-Content -Path $script -Encoding ascii

    cmd.exe /c "`"$script`" > `"$buildLog`" 2>&1" | Out-Null

    $output = if (Test-Path $buildLog) { Get-Content $buildLog } else { @() }

    # Derleme basarisiz olursa kurulum yine de tamamlaniyor: sunucu
    # ayakta kaliyor ve pgvector sonradan eklenebiliyor. Ama uyarinin
    # ne dedigi onemli - bkz. Write-PgvectorMissing.
    if (-not (Test-Path $control)) {
        Write-PgvectorMissing 'Derleme basarisiz.'
        $output | Select-Object -Last 6 | ForEach-Object { Write-Host "    $_" }
        return
    }

    Write-Step 'pgvector kuruldu.'
}

# Neden kurulamadigini ve NEYIN ETKILENDIGINI soyler.
#
# ETKI TAM: pgvector yalnizca birkac testi degil, SEMANIN KENDISINI
# engelliyor - ilk migration `CREATE EXTENSION vector` calistiriyor ve
# eklenti yoksa hicbir tablo olusmuyor. Yani pgvector'suz bir yerel
# PostgreSQL bu projede ise yaramiyor.
#
# Bunu "birkac test kosmaz" diye yazmak yaniltici olurdu; ilk hali
# oyle yaziyordu ve gercegi ancak migration denenince ortaya cikti.
function Write-PgvectorMissing([string]$Reason) {
    Write-Warning "pgvector KURULMADI - $Reason"
    Write-Warning '  Sunucu ayakta ama SEMA KURULAMAZ: ilk migration'
    Write-Warning '  `CREATE EXTENSION vector` calistiriyor.'
    Write-Warning '  Veritabani gerektiren testler CI''da kosmaya devam ediyor.'
    Write-Warning '  Yerelde acmak icin Windows SDK + MSVC gerekiyor:'
    Write-Warning '    winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"'
    Write-Warning '  Kurulduktan sonra bu betigi tekrar calistirin.'
}

function Initialize-Database {
    # Eklentiler TEMPLATE1'e kuruluyor: CI ile ayni. Boylece her yeni
    # test veritabani onlarla doguyor ve `CREATE EXTENSION` yetkisi
    # gerekmiyor.
    $extensions = @('pg_trgm', 'uuid-ossp')

    if (Test-Path (Join-Path $PgRoot 'share\extension\vector.control')) {
        $extensions = @('vector') + $extensions
    }

    foreach ($extension in $extensions) {
        Invoke-Psql -Db 'template1' -Sql "CREATE EXTENSION IF NOT EXISTS `"$extension`";" | Out-Null
    }

    Write-Step "Eklentiler hazir: $($extensions -join ', ')"

    $exists = Invoke-Psql -Db 'postgres' -Sql "SELECT 1 FROM pg_database WHERE datname = '$Database';"

    # `-t -A` ile cikti yalnizca degerin kendisi: `1` ya da bos.
    if ("$exists".Trim() -ne '1') {
        Invoke-Psql -Db 'postgres' -Sql "CREATE DATABASE `"$Database`";" | Out-Null
        Write-Step "Veritabani olusturuldu: $Database"
    }
    else {
        Write-Step "Veritabani zaten var: $Database"
    }
}

Write-Host ''

switch ($Action) {
    'status' {
        if (-not (Test-Path (Join-Path $Bin 'postgres.exe'))) {
            Write-Step 'Kurulu degil. `scripts/yerel-postgres.ps1` calistirin.'
            break
        }

        Write-Step "Klasor  : $Root"
        Write-Step "Durum   : $(if (Test-Running) { "calisiyor (port $Port)" } else { 'durmus' })"
        Write-Step "pgvector: $(if (Test-Path (Join-Path $PgRoot 'share\extension\vector.control')) { 'kurulu' } else { 'YOK' })"
    }

    'start' { Start-Server }

    'stop' { Stop-Server }

    'remove' {
        if (Test-Running) { Stop-Server }
        Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
        Write-Step "Silindi: $Root"
    }

    'setup' {
        Install-Binaries
        Initialize-Cluster
        Install-Pgvector
        Start-Server
        Initialize-Database

        Write-Host ''
        Write-Step 'Hazir. Baglanti dizesi zaten varsayilan:'
        Write-Step "  Host=localhost;Port=$Port;Database=$Database;Username=$User;Password=$Password"
        Write-Host ''
        Write-Step 'Durdurmak icin: pwsh -File scripts/yerel-postgres.ps1 -Action stop'
    }
}

Write-Host ''
