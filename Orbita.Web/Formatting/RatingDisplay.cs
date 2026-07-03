namespace Orbita.Web.Formatting;

public static class RatingDisplay
{
    public static string? FormatSubProfile(decimal? rating, int? reviewsCount, string? reviewsText)
    {
        if (!rating.HasValue)
        {
            return null;
        }

        var ratingPart = rating.Value.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        if (!string.IsNullOrWhiteSpace(reviewsText))
        {
            return $"{ratingPart} ★ · {reviewsText.Trim()}";
        }

        if (reviewsCount is int count)
        {
            return count == 0
                ? $"{ratingPart} ★ · нет отзывов"
                : $"{ratingPart} ★ · {count} отз.";
        }

        return $"{ratingPart} ★";
    }
}