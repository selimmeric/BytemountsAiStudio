using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Providers.Fake;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Node işleyicilerinin kaydı.
///
/// Kayıt tek yerde, çünkü workflow doğrulaması bilinen tipleri buradan
/// alıyor: kayıtlı olmayan bir node tipine sahip graf hiç kaydedilemiyor
/// (§6.2). İki ayrı yerde kayıt yapılsaydı, biri eksik kalır ve hata ancak
/// run'ın ortasında ortaya çıkardı.
public static class NodeHandlerRegistration
{
    /// Faz 0'ın sahte hattı: tüm sağlayıcılar fake, ağa çıkılmıyor.
    public static NodeRegistry BuildFakeRegistry(
        IStorageProvider storage,
        string outputDirectory,
        string ffmpegPath = "ffmpeg",
        string ffprobePath = "ffprobe")
        => new NodeRegistry()
            .Register(new TopicSelectHandler())
            .Register(new ResearchHandler())
            .Register(new ScriptGenerateHandler(new FakeLlmProvider()))
            .Register(new TtsSynthesizeHandler(new FakeTtsProvider(), storage, ffprobePath))
            .Register(new VisualResolveHandler(new FakeImageProvider(ImageProviderKind.Generative), storage))
            .Register(new TimelineCompileHandler(storage))
            .Register(new MediaRenderHandler(storage, outputDirectory, ffmpegPath, ffprobePath));

    /// Yalnızca graf doğrulaması için: hangi node tipleri tanınıyor.
    /// Depolama gerektirmediği için konfigürasyon aşamasında kullanılabilir.
    public static IReadOnlySet<string> KnownNodeTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "topic.select",
        "research.deep",
        "script.generate",
        "tts.synthesize",
        "visual.resolve",
        "timeline.compile",
        "media.render",
    };
}
