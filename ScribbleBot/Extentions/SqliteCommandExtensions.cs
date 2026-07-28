using System.Text;
using Microsoft.Data.Sqlite;

namespace ScribbleBot.Extensions;

public static class SqliteCommandExtensions
{
    /// <summary>
    /// Reconstructs the full SQL command string with parameter values expanded for logging.
    /// </summary>
    public static string ToFullCommandText(this SqliteCommand command)
    {
        if (command == null) return string.Empty;

        var sql = new StringBuilder(command.CommandText);

        foreach (SqliteParameter param in command.Parameters)
        {
            string paramName = param.ParameterName;
            if (!paramName.StartsWith("@") && !paramName.StartsWith("$") && !paramName.StartsWith(":"))
            {
                paramName = "@" + paramName;
            }

            string formattedValue = FormatValue(param.Value);
            sql.Replace(paramName, formattedValue);
        }

        return sql.ToString();
    }

    private static string FormatValue(object? value)
    {
        if (value == null || value == DBNull.Value)
            return "NULL";

        return value switch
        {
            string str => $"'{str.Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.FFF}'",
            bool b => b ? "1" : "0",
            byte[] bytes => $"X'{Convert.ToHexString(bytes)}'", // SQLite BLOB literal
            Enum e => Convert.ToInt64(e).ToString(),
            _ => value.ToString() ?? "NULL"
        };
    }
}