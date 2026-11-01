using System.Globalization;
using System.Data.Common;

namespace NeoSkin.Web.Db;

/// <summary>
/// 读取结果的 PHP 语义归一化。
///
/// <p>PHP PDO 把 <c>timestamp</c> 列当字符串返回；Npgsql 会返回 <see cref="DateTime"/>，
/// <c>ToString()</c> 又依赖当前区域性。仓库层统一经这里转成 <c>Y-m-d H:i:s</c>，
/// 保证五种驱动的 JSON/模板输出一致。</p>
/// </summary>
public static class DbRead
{
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>非空字符串；NULL 返回空串。</summary>
    public static string Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Format(reader.GetValue(ordinal)) ?? "";

    /// <summary>可空字符串；NULL 返回 <c>null</c>。</summary>
    public static string? TextOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Format(reader.GetValue(ordinal));

    /// <summary>整数（INT / BIGINT / SERIAL / IDENTITY 统一成 long）。</summary>
    public static long Long(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>整数（统一成 int）。</summary>
    public static int Int(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? 0
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>把驱动返回值归一化为 PHP 会输出的字符串/数值。</summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        DateTime dateTime => dateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        _ => value,
    };

    private static string? Format(object? value) => Normalize(value) switch
    {
        null => null,
        string s => s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        object other => other.ToString(),
    };
}
