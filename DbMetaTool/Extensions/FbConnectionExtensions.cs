using System.Text;
using FirebirdSql.Data.FirebirdClient;

namespace DbMetaTool.Extensions;

public static class FbConnectionExtensions
{
    private const string Domains =
        """
        SELECT
            RDB$FIELD_NAME,
            RDB$FIELD_TYPE,
            RDB$FIELD_LENGTH,
            RDB$NULL_FLAG,
            RDB$FIELD_SUB_TYPE,
            RDB$FIELD_PRECISION,
            RDB$FIELD_SCALE,
            RDB$DEFAULT_SOURCE,
            RDB$VALIDATION_SOURCE
        FROM RDB$FIELDS
        WHERE RDB$SYSTEM_FLAG = 0 AND RDB$FIELD_NAME NOT STARTING WITH 'RDB$'
        """;

    private const string TablesWithColumns =
        """
        SELECT
            r.RDB$RELATION_NAME,
            rf.RDB$FIELD_NAME,
            rf.RDB$FIELD_SOURCE,
            f.RDB$FIELD_TYPE,
            f.RDB$FIELD_LENGTH,
            f.RDB$FIELD_SUB_TYPE,
            f.RDB$FIELD_PRECISION,
            f.RDB$FIELD_SCALE,
            f.RDB$SYSTEM_FLAG
        FROM RDB$RELATIONS r
        JOIN RDB$RELATION_FIELDS rf
            ON rf.RDB$RELATION_NAME = r.RDB$RELATION_NAME
        JOIN RDB$FIELDS f
            ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
        WHERE r.RDB$SYSTEM_FLAG = 0
        AND r.RDB$VIEW_BLR IS NULL
        ORDER BY r.RDB$RELATION_NAME, rf.RDB$FIELD_POSITION
        """;

    private const string Procedures =
        """
        SELECT
            RDB$PROCEDURE_NAME,
            RDB$PROCEDURE_SOURCE
        FROM RDB$PROCEDURES
        WHERE RDB$SYSTEM_FLAG = 0
        """;

    private const string ProcedureParameters =
        """
        SELECT
            pp.RDB$PROCEDURE_NAME,
            pp.RDB$PARAMETER_NAME,
            pp.RDB$PARAMETER_TYPE,
            f.RDB$FIELD_TYPE,
            f.RDB$FIELD_LENGTH,
            f.RDB$FIELD_SUB_TYPE,
            f.RDB$FIELD_PRECISION,
            f.RDB$FIELD_SCALE
        FROM RDB$PROCEDURE_PARAMETERS pp
        JOIN RDB$FIELDS f
            ON f.RDB$FIELD_NAME = pp.RDB$FIELD_SOURCE
        ORDER BY pp.RDB$PROCEDURE_NAME, pp.RDB$PARAMETER_TYPE, pp.RDB$PARAMETER_NUMBER
        """;

    #region Read

    public static string ExportDomains(this FbConnection connection)
    {
        var sql = new StringBuilder();

        using var command = new FbCommand(Domains, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var name = reader.GetString(0).Trim();
            var fieldType = reader.GetInt32(1);
            var length = reader.GetInt32(2);
            var subType = reader.GetInt32OrDefault(4);
            var precision = reader.GetInt32OrDefault(5);
            var scale = reader.GetInt32OrDefault(6);

            var type = GetFirebirdType(fieldType, length, subType, precision, scale);
            sql.Append($"CREATE DOMAIN {name} AS {type}");

            // DEFAULT
            if (!reader.IsDBNull(7))
            {
                var defaultValue = reader.GetString(7).Trim();
                sql.Append($" {defaultValue}");
            }

            // NOT NULL
            if (!reader.IsDBNull(3) && reader.GetInt32(3) == 1)
            {
                sql.Append(" NOT NULL");
            }

            // CHECK
            if (!reader.IsDBNull(8))
            {
                var checkConstraint = reader.GetString(8).Trim();
                sql.Append($" {checkConstraint}");
            }

            sql.AppendLine(";");
            sql.AppendLine();
        }

        return sql.ToString();
    }

    public static string ExportTables(this FbConnection connection)
    {
        var sql = new StringBuilder();

        using var command = new FbCommand(TablesWithColumns, connection);
        using var reader = command.ExecuteReader();

        string? currentTable = null;
        var columns = new List<string>();

        while (reader.Read())
        {
            var tableName = reader.GetString(0).Trim();

            if (currentTable is not null && tableName != currentTable)
            {
                AppendTableScript(sql, currentTable, columns);
                columns.Clear();
            }

            currentTable = tableName;
            var columnName = reader.GetString(1).Trim();
            var domainOrSource = reader.GetString(2).Trim();

            var fieldType = reader.GetInt32(3);
            var length = reader.GetInt32(4);
            var subType = reader.GetInt32OrDefault(5);
            var precision = reader.GetInt32OrDefault(6);
            var scale = reader.GetInt32OrDefault(7);

            var isUserDefinedDomain = reader.GetInt32(8) == 0 && !domainOrSource.StartsWith("RDB$");

            // Jeśli kolumna używa domeny stworzonej przez użytkownika (np. D_KWOTA), używamy jej nazwy.
            // W przeciwnym razie jest to typ wpisany bezpośrednio ("z palca"), więc zamieniamy go na typ SQL (np. NUMERIC(15, 2)).
            var typeOrDomain =
                isUserDefinedDomain ? domainOrSource : GetFirebirdType(fieldType, length, subType, precision, scale);

            columns.Add($"    {columnName} {typeOrDomain}");
        }

        if (currentTable is null)
            return sql.ToString();

        AppendTableScript(sql, currentTable, columns);

        return sql.ToString();
    }


    public static string ExportProcedures(this FbConnection connection)
    {
        var sql = new StringBuilder();
        var procedures = new List<(string Name, string? Source)>();

        using (var command = new FbCommand(Procedures, connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0).Trim(); // RDB$PROCEDURE_NAME
                var source = reader.IsDBNull(1) ? null : reader.GetString(1); // RDB$PROCEDURE_SOURCE
                procedures.Add((name, source));
            }
        }

        if (procedures.Count == 0)
            return string.Empty;

        var parameters = GetAllProcedureParameters(connection);

        sql.AppendLine("SET TERM ^ ;");
        sql.AppendLine();

        foreach (var (name, source) in procedures)
        {
            var (inputs, outputs) = parameters.TryGetValue(name, out var found)
                ? found
                : (new List<string>(), new List<string>());

            sql.Append($"CREATE OR ALTER PROCEDURE {name} ");

            if (inputs.Count > 0)
                sql.Append($"({string.Join(", ", inputs)}) ");

            if (outputs.Count > 0)
                sql.Append($"RETURNS ({string.Join(", ", outputs)}) ");

            sql.AppendLine();
            sql.AppendLine("AS");

            var cleanSource = source?.TrimEnd(';', ' ', '\r', '\n');
            sql.AppendLine($"{cleanSource}^");
            sql.AppendLine();
        }

        sql.AppendLine("SET TERM ; ^");

        return sql.ToString();
    }

    /// <summary>
    /// Pobiera parametry wejściowe i wyjściowe wszystkich procedur jednym zapytaniem (JOIN),
    /// zamiast osobnego zapytania per procedura, i grupuje je po nazwie procedury.
    /// </summary>
    private static Dictionary<string, (List<string> Inputs, List<string> Outputs)> GetAllProcedureParameters(
        FbConnection connection)
    {
        var result = new Dictionary<string, (List<string> Inputs, List<string> Outputs)>();

        using var command = new FbCommand(ProcedureParameters, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var procedureName = reader.GetString(0).Trim(); // RDB$PROCEDURE_NAME
            var paramName = reader.GetString(1).Trim(); // RDB$PARAMETER_NAME
            var paramType = reader.GetInt32(2); // RDB$PARAMETER_TYPE (0 = input, 1 = output)
            var fieldType = reader.GetInt32(3); // RDB$FIELD_TYPE
            var length = reader.GetInt32OrDefault(4); // RDB$FIELD_LENGTH
            var subType = reader.GetInt32OrDefault(5); // RDB$FIELD_SUB_TYPE
            var precision = reader.GetInt32OrDefault(6); // RDB$FIELD_PRECISION
            var scale = reader.GetInt32OrDefault(7); // RDB$FIELD_SCALE

            var declaration = $"{paramName} {GetFirebirdType(fieldType, length, subType, precision, scale)}";

            if (!result.TryGetValue(procedureName, out var lists))
                result[procedureName] = lists = ([], []);

            if (paramType == 0)
                lists.Inputs.Add(declaration);
            else
                lists.Outputs.Add(declaration);
        }

        return result;
    }

    private static void AppendTableScript(StringBuilder sql, string tableName, List<string> columns)
    {
        sql.AppendLine($"CREATE TABLE {tableName}");
        sql.AppendLine("(");
        sql.AppendLine(string.Join(",\n", columns));
        sql.AppendLine(");");
        sql.AppendLine();
    }

    /// <summary>
    /// Pobiera wartość <see cref="int"/> z podanej kolumny lub zwraca wartość domyślną, jeśli wartość w bazie to <c>NULL</c>.
    /// </summary>
    private static int GetInt32OrDefault(this FbDataReader reader, int index, int defaultValue = 0)
        => reader.IsDBNull(index) ? defaultValue : reader.GetInt32(index);

    /// <summary>
    /// Mapuje wewnętrzny identyfikator typu danych Firebird (<c>RDB$FIELD_TYPE</c>) na jego odpowiednik SQL.
    /// Pełna specyfikacja typów: <see href="https://www.firebirdsql.org/file/documentation/chunk/en/refdocs/fblangref30/fblangref-appx04-fields.html">RDB$FIELDS Reference</see>.
    /// </summary>
    private static string GetFirebirdType(int fieldType, int length, int subType = 0, int precision = 0, int scale = 0)
        => fieldType switch
        {
            7 or 8 or 16 when subType is 1 or 2 => $"{(subType == 1 ? "NUMERIC" : "DECIMAL")}({precision},{-scale})",
            7 => "SMALLINT",
            8 => "INTEGER",
            16 => "BIGINT",
            10 => "FLOAT",
            27 => "DOUBLE PRECISION",
            12 => "DATE",
            13 => "TIME",
            35 => "TIMESTAMP",
            14 => $"CHAR({length})",
            37 => $"VARCHAR({length})",
            261 => "BLOB",
            _ => throw new NotSupportedException($"Niewspierany typ Firebird {fieldType}")
        };
}

#endregion