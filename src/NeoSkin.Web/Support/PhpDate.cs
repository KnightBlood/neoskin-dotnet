using NodaTime;
using NodaTime.Text;

namespace NeoSkin.Web.Support;

/// <summary>
/// PHP / Laravel 的时间格式工具。
///
/// <p>源项目所有 <c>timestamp</c> / <c>dateTime</c> 列都用 Laravel 默认的
/// <c>Y-m-d H:i:s</c> 文本格式存储，<c>now()</c> 用 <c>config('app.timezone')</c>
///（源项目固定 <c>Asia/Shanghai</c>）。所以写库时必须：</p>
/// <list type="number">
///   <item>用应用时区而不是宿主默认时区（容器里是 UTC 会差 8 小时）；</item>
///   <item>用 <c>yyyy-MM-dd HH:mm:ss</c> 而不是 ISO-8601（否则 PHP 版读不了，字符串排序也会错）。</item>
/// </list>
///
/// <p>用 <b>NodaTime</b> 而不是 <c>TimeZoneInfo</c>：Windows 的时区库没有 IANA 历史
/// 数据，例如 <c>Asia/Shanghai</c> 在 1901 年之前是 LMT <c>+08:05:43</c>，
/// 用 <c>TimeZoneInfo</c> 会得到 <c>+08:00</c>，与 PHP 差 343 秒
/// （语料 <c>datetime.json</c> 的 <c>format.neg_1900</c> 锁定）。</p>
/// </summary>
public static class PhpDate
{
    /// <summary>Laravel 的默认 <c>$dateFormat</c>。</summary>
    public const string Format = "yyyy-MM-dd HH:mm:ss";

    private static readonly LocalDateTimePattern Pattern =
        LocalDateTimePattern.CreateWithInvariantCulture(Format);

    /// <summary>按应用时区取当前时间，格式化为 <c>Y-m-d H:i:s</c>。</summary>
    public static string Now(string? timezone) => At(DateTimeOffset.UtcNow, timezone);

    /// <summary>把某个时刻按应用时区格式化。</summary>
    public static string At(DateTimeOffset instant, string? timezone)
    {
        Instant i = Instant.FromUnixTimeMilliseconds(instant.ToUnixTimeMilliseconds());
        ZonedDateTime zoned = i.InZone(Zone(timezone));
        return Pattern.Format(zoned.LocalDateTime);
    }

    /// <summary>把毫秒时间戳按应用时区格式化。</summary>
    public static string At(long epochMillis, string? timezone) =>
        At(DateTimeOffset.FromUnixTimeMilliseconds(epochMillis), timezone);

    /// <summary>解析 <c>Y-m-d H:i:s</c>；无法解析时返回 <c>null</c>。</summary>
    public static DateTime? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ParseResult<LocalDateTime> parsed = Pattern.Parse(text.Trim());
        return parsed.Success ? parsed.Value.ToDateTimeUnspecified() : null;
    }

    /// <summary>取某时区的年份（对应 <c>Carbon::now()->year</c>）。</summary>
    public static int CurrentYear(string? timezone) =>
        Instant.FromDateTimeOffset(DateTimeOffset.UtcNow).InZone(Zone(timezone)).Year;

    /// <summary>时区解析：非法值回退到系统时区而不是抛异常。</summary>
    public static DateTimeZone Zone(string? timezone)
    {
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            DateTimeZone? found = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timezone.Trim());
            if (found is not null)
            {
                return found;
            }
        }

        try
        {
            return DateTimeZoneProviders.Tzdb.GetSystemDefault();
        }
        catch (Exception)
        {
            return DateTimeZone.Utc;
        }
    }
}
