using System.Text;

namespace LeadFlow.Services;

public sealed class PhoneNormalizer : IPhoneNormalizer
{
    public string Normalize(string phone)
    {
        var digits = new StringBuilder();
        foreach (var ch in phone)
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
            }
        }

        var value = digits.ToString();
        if (value.Length == 11 && value.StartsWith('8'))
        {
            value = $"7{value[1..]}";
        }
        else if (value.Length == 10)
        {
            value = $"7{value}";
        }

        return value;
    }
}
