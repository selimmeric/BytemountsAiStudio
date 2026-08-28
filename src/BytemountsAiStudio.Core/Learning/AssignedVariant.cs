using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Learning;

/// Bir run'ın bir deneyde aldığı kol (P5-03).
public readonly record struct AssignedVariant(
    Guid ExperimentId,
    Guid VariantId,
    string Dimension,
    string VariantName,
    string ConfigJson);

/// Hangi boyutun hangi ayarları kabul ettiği (P5-03).
///
/// TANINMAYAN BOYUT SESSİZCE GEÇMİYOR. Geçseydi, o boyutun ayarları
/// hiç doğrulanmaz ve yazım hatası olan bir varyant iki kolda da aynı
/// videoyu üretirdi — deneyin ölçtüğü şey hiçbir şey olurdu.
public static class VariantVocabulary
{
    public static Result<IReadOnlyDictionary<string, string[]>> For(string dimension)
        => dimension switch
        {
            "thumbnail" => Result.Success(ThumbnailVariant.Allowed),
            "title" => Result.Success(TitleVariant.Allowed),

            "prompt" => Error.Permanent("variant.dimension_unsupported",
                "İstem boyutu henüz bağlanmadı (P5-05). Deney koşturmak, "
                + "ayarları doğrulanmamış bir varyantla ölçüm yapmak olurdu."),

            _ => Error.Permanent("variant.unknown_dimension",
                $"'{dimension}' bilinmeyen bir deney boyutu. Tanımlılar: thumbnail, title."),
        };
}
