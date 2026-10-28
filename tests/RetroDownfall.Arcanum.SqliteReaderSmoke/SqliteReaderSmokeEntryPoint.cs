using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.SqliteReaderSmoke;

public static class SqliteReaderSmokeEntryPoint
{
    public static int Main(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            return 2;
        }

        try
        {
            Run(arguments[0]);

            Console.WriteLine("SQLITE_READER_SMOKE_OK " + arguments[0]);

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Sqlite reader smoke failed: " + exception.GetType().Name);

            return 1;
        }
    }

    private static void Run(string mode)
    {
        bool table = mode is "table-zero" or "table-three" or "table-stream" or "disposed-stream";

        if (!table && mode is not ("computed-fixed" or "computed-length" or "computed-stream" or "computed-alias" or "single-expression" or "computed-empty" or "null-value" or "bad-ordinal"
            or "negative-offset" or "bad-buffer" or "numeric-conversion" or "cancelled" or "disposed-reader"))
        {
            throw new ArgumentException("Unknown closed smoke case.", nameof(mode));
        }

        SqliteNativeRuntime.Instance.Initialize();

        using SqliteConnection connection = new("Data Source=:memory:");

        connection.Open();

        byte[] expected = Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();

        using SqliteCommand command = connection.CreateCommand();

        if (table)
        {
            command.CommandText = "CREATE TABLE probe(Id INTEGER PRIMARY KEY, Payload BLOB); INSERT INTO probe VALUES(1,$payload);";

            command.Parameters.AddWithValue("$payload", expected);

            command.ExecuteNonQuery();

            command.Parameters.Clear();
        }

        command.CommandText = mode switch
        {
            "table-zero" => "SELECT Payload,NULL FROM probe;",
            "table-three" => "SELECT 1,2,3,Payload,NULL FROM probe;",
            "table-stream" or "disposed-stream" => "SELECT Id,Payload,NULL FROM probe;",
            "null-value" => "SELECT NULL;",
            "numeric-conversion" => "SELECT 12345;",
            "computed-empty" => "SELECT zeroblob(0),NULL;",
            "single-expression" or "bad-ordinal" or "negative-offset" or "bad-buffer" or "disposed-reader" => "SELECT x'0102030405060708090A0B0C0D0E0F10';",
            "computed-alias" => "SELECT x'0102030405060708090A0B0C0D0E0F10' AS Payload,1 AS Sibling;",
            _ => "SELECT x'0102030405060708090A0B0C0D0E0F10',NULL;",
        };

        if (mode == "cancelled")
        {
            using CancellationTokenSource cancelled = new();

            cancelled.Cancel();

            try
            {
                command.ExecuteReaderAsync(cancelled.Token).GetAwaiter().GetResult();

                throw new InvalidDataException("Cancellation did not stop dispatch.");
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancelled.Token)
            {
                VerifyConnection(connection);

                return;
            }
        }

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            throw new InvalidDataException("The smoke row is absent.");
        }

        int ordinal = mode == "table-three" ? 3 : mode is "table-stream" or "disposed-stream" ? 1 : 0;

        if (mode == "null-value")
        {
            Expect<InvalidOperationException>(() => reader.GetStream(0));

            return;
        }

        if (mode == "bad-ordinal")
        {
            Expect<ArgumentOutOfRangeException>(() => reader.GetStream(-1));

            return;
        }

        if (mode == "negative-offset")
        {
            Expect<ArgumentOutOfRangeException>(() => reader.GetBytes(0, -1, new byte[16], 0, 16));

            return;
        }

        if (mode == "bad-buffer")
        {
            Expect<ArgumentOutOfRangeException>(() => reader.GetBytes(0, 0, new byte[16], 8, 16));

            return;
        }

        if (mode == "disposed-reader")
        {
            reader.Dispose();

            Expect<InvalidOperationException>(() => reader.GetBytes(0, 0, new byte[16], 0, 16));

            VerifyConnection(connection);

            return;
        }

        if (mode == "disposed-stream")
        {
            Stream disposed = reader.GetStream(ordinal);

            disposed.Dispose();

            Expect<ObjectDisposedException>(() => disposed.ReadByte());

            VerifyConnection(connection);

            return;
        }

        if (mode == "computed-empty")
        {
            if (reader.GetBytes(0, 0, null, 0, 0) != 0 || reader.GetBytes(0, 0, new byte[16], 0, 16) != 0)
            {
                throw new InvalidDataException("The empty BLOB is not empty.");
            }

            return;
        }

        if (mode == "numeric-conversion")
        {
            byte[] numeric = new byte[5];

            if (reader.GetBytes(0, 0, numeric, 0, 5) != 5 || !numeric.AsSpan().SequenceEqual(new byte[] { 49, 50, 51, 52, 53 }))
            {
                throw new InvalidDataException("Numeric conversion changed.");
            }

            return;
        }

        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidDataException("The smoke BLOB is null.");
        }

        if (mode == "computed-length")
        {
            if (reader.GetBytes(ordinal, 0, null, 0, 0) != 16)
            {
                throw new InvalidDataException("The BLOB length differs.");
            }

            return;
        }

        if (mode is "computed-stream" or "table-stream")
        {
            using Stream stream = reader.GetStream(ordinal);

            if (!stream.CanRead || !stream.CanSeek || stream.CanWrite)
            {
                throw new InvalidDataException("The reader stream capabilities changed.");
            }

            if (mode == "table-stream" && stream is not SqliteBlob)
            {
                throw new InvalidDataException("Table streaming stopped using SqliteBlob.");
            }

            stream.Position = 4;

            byte[] segment = new byte[8];

            stream.ReadExactly(segment);

            if (!segment.AsSpan().SequenceEqual(expected.AsSpan(4, 8)))
            {
                throw new InvalidDataException("The stream segment differs.");
            }

            return;
        }

        byte[] actual = new byte[16];

        if (reader.GetBytes(ordinal, 0, actual, 0, actual.Length) != 16 || !actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException("The BLOB bytes differ.");
        }
    }

    private static void VerifyConnection(SqliteConnection connection)
    {
        using SqliteCommand check = connection.CreateCommand();

        check.CommandText = "SELECT 1;";

        if (check.ExecuteScalar() is not long value || value != 1)
        {
            throw new InvalidDataException("The connection is not usable after the boundary.");
        }
    }

    private static void Expect<TException>(Action operation) where TException : Exception
    {
        try
        {
            operation();
        }
        catch (Exception exception) when (exception.GetType() == typeof(TException))
        {
            return;
        }

        throw new InvalidDataException("The expected typed refusal did not occur.");
    }
}
