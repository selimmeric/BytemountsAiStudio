using System.Reflection;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Workflow.Tests;

/// ADR-004'ün kendi iddiasının bekçisi (P4-08).
///
/// ADR-004 şöyle diyor: *"Kendi ince DAG engine'imiz… `IWorkflowEngine`
/// arayüzü arkasında saklanır ki Faz 4'te Temporal'a geçiş bir
/// implementasyon değişimi olsun."*
///
/// İDDİA ÖLÇÜLDÜĞÜNDE YANLIŞTI. `ApprovalService` ve
/// `DeadLetterTriage` somut `WorkflowEngine` sınıfına bağlıydı, çünkü
/// kullandıkları `EnqueueAfterAsync` arayüzde yoktu. Motoru
/// değiştirmek o iki servisi de yeniden yazmak demekti — yani
/// "implementasyon değişimi" değil.
///
/// Bu test o sızıntının geri gelmesini engelliyor. Bir mimari kararın
/// doğru KALMASI, yazılmasıyla değil sınanmasıyla oluyor — bu depoda
/// yorumu doğru, davranışı yanlış olan kod defalarca çıktı.
public sealed class EngineSeamTests
{
    /// Motor dışındaki hiçbir tip somut `WorkflowEngine`'e bağlı
    /// olmamalı.
    ///
    /// KURUCU PARAMETRELERİNE BAKIYOR: bir servisin motora bağlanma
    /// yolu budur. Alan ya da özellik üzerinden bağlanmak da mümkün
    /// ama bu depoda bağımlılıklar birincil kurucudan geliyor.
    [Fact]
    public void MotorDisindakiTipler_SomutSinifaBagliDegil()
    {
        var assembly = typeof(IWorkflowEngine).Assembly;
        var concrete = typeof(WorkflowEngine);

        var leaks = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type == concrete)
            {
                continue;
            }

            foreach (var constructor in type.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.ParameterType == concrete)
                    {
                        leaks.Add($"{type.Name}.{constructor.Name}({parameter.Name})");
                    }
                }
            }
        }

        Assert.True(leaks.Count == 0,
            "Şu tipler somut `WorkflowEngine`'e bağlı; motoru değiştirmek "
            + "onları da yeniden yazmak demek: " + string.Join(", ", leaks));
    }

    /// SERVİSLERİN KULLANDIĞI HER METOT ARAYÜZDE.
    ///
    /// Aksi hâlde arayüz motorun GERÇEK yüzeyini göstermiyor demektir
    /// ve "arayüz arkasında" iddiası yalnızca kâğıt üstünde kalır.
    [Fact]
    public void ArayuzGercekYuzeyiGosteriyor()
    {
        var methods = typeof(IWorkflowEngine)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Onay ve ölü mektup servisleri bu üçünü kullanıyor.
        Assert.Contains("StartRunAsync", methods);
        Assert.Contains("ExecuteNextAsync", methods);
        Assert.Contains("EnqueueAfterAsync", methods);
    }

    /// ARAYÜZ KÜÇÜK KALIYOR.
    ///
    /// Bu test bir sayıyı değil bir EĞİLİMİ koruyor: arayüz her yeni
    /// ihtiyaçta büyürse, "başka bir motora geçilebilir" iddiası
    /// sessizce imkânsızlaşır. Sınır aşıldığında karar bilinçli
    /// olmalı — testi değiştirmek o kararı görünür kılıyor.
    ///
    /// Bugünkü gerçek: üç metot. `EnqueueAfterAsync`'in imzası `Run`
    /// ve `WorkflowGraph` taşıyor, yani zaten model-bağımlı; arayüz
    /// gerçek yüzeyi gösteriyor, KÜÇÜLTMÜYOR.
    [Fact]
    public void Arayuz_KucukKaliyor()
        => Assert.True(typeof(IWorkflowEngine).GetMethods().Length <= 4,
            "Motor arayüzü büyüyor; başka bir motora geçiş iddiası zayıflıyor.");
}
