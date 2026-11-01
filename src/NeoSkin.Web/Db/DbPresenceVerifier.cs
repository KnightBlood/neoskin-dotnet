using System.Collections.Generic;
using NeoSkin.Web.Validation;

namespace NeoSkin.Web.Db;

/// <summary>
/// {@code unique} / {@code exists} 规则用的数据库存在性验证器（源 <c>JdbcPresenceVerifier</c>）。
///
/// <p>按 {@code table}/{column} 做 {@code COUNT(*)} 查询，支持按主键排除一行。</p>
/// </summary>
public sealed class DbPresenceVerifier(Database db) : IPresenceVerifier
{
    public int GetCount(string table, string column, object? value, object? excludeId,
        string idColumn)
    {
        var sql = "SELECT COUNT(*) FROM " + table + " WHERE " + column + " = @v";
        var parameters = new List<(string, object?)> { ("@v", value ?? DBNull.Value) };

        if (excludeId is not null)
        {
            sql += " AND " + idColumn + " <> @x";
            parameters.Add(("@x", excludeId));
        }

        return (int)db.Long(sql, parameters.ToArray());
    }
}
