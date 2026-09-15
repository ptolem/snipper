namespace CoreLib;

// Regression for SNP0002: a static local function declared after the return,
// followed by a stray semicolon. The `;` parses as an empty statement — a
// token artifact, not code that can never execute.
public static class OrderNumberDecoder
{
    private const string CustomBase32Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ1234567890";

    public static DateTimeOffset GetOrderGeneratedAt(string orderNumber, TimeZoneInfo? timeZoneInfo = null)
    {
        var year = int.Parse("20" + orderNumber.Substring(0, 2));
        var startOfYear = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var secondsInYearEncoded = orderNumber.Substring(2, 5);
        var secondsInYear = CustomBase32Decode(secondsInYearEncoded);
        var orderGeneratedAtUtc = startOfYear.AddSeconds(secondsInYear);
        return TimeZoneInfo.ConvertTime(orderGeneratedAtUtc, timeZoneInfo ?? TimeZoneInfo.Local);
        static int CustomBase32Decode(string encoded)
        {
            var total = 0;
            var power = 0;
            for (var i = encoded.Length - 1; i >= 0; i--)
            {
                if (encoded[i] == '0')
                    break;
                total += (int)Math.Pow(32, power++) * CustomBase32Alphabet.IndexOf(encoded[i]);
            }
            return total;
        };
    }
}
