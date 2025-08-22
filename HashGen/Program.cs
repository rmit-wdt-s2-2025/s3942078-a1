// 需要 .NET 9
// NuGet: Microsoft.Data.SqlClient, SimpleHashing.Net

using Microsoft.Data.SqlClient;
using SimpleHashing.Net;
using System.Globalization;

class Program
{
    static async Task Main()
    {
        // ① 连接串：把 <你的appuser密码> 改成实际密码
        var cs =
            "Server=tcp:wdtbank-srv-oscar-eastaustralia.database.windows.net,1433;" +
            "Initial Catalog=wdtbank-db;" +
            "User ID=appuser;Password=@bcD1234;" +
            "Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ② 登录（校验 Login 表的 94 位哈希）
        Console.Write("LoginID (8位): ");
        var loginId = Console.ReadLine()!.Trim();

        Console.Write("Password: ");
        var password = ReadHidden();

        int customerId;

        await using (var cn = new SqlConnection(cs))
        {
            await cn.OpenAsync();

            string? storedHash = null;
            await using (var cmd = new SqlCommand(
                "SELECT PasswordHash, CustomerID FROM dbo.Login WHERE LoginID=@id", cn))
            {
                cmd.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Char, 8) { Value = loginId });
                using var rd = await cmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    storedHash = rd.GetString(0);
                    customerId = rd.GetInt32(1);
                }
                else
                {
                    Console.WriteLine(" LoginID does not exist");
                    return;
                }
            }

            if (!new SimpleHash().Verify(password, storedHash!.TrimEnd()))
            {
                Console.WriteLine(" wrong password");
                return;
            }

            Console.WriteLine($" Login successful，CustomerID={customerId}");

            // ③ 主菜单
            while (true)
            {
                Console.WriteLine("\n==== menu ====");
                Console.WriteLine("1) account");
                Console.WriteLine("2) Deposit");
                Console.WriteLine("3) withdraw");
                Console.WriteLine("4) transfer");
                Console.WriteLine("5) Statistics of W/T times in the current month");
                Console.WriteLine("0) exit");
                Console.Write("select: ");
                var choice = Console.ReadLine();

                try
                {
                    switch (choice)
                    {
                        case "1":
                            await ShowAccounts(cn, customerId);
                            break;
                        case "2":
                            await DepositFlow(cn, customerId);
                            break;
                        case "3":
                            await WithdrawFlow(cn, customerId);
                            break;
                        case "4":
                            await TransferFlow(cn, customerId);
                            break;
                        case "5":
                            await ShowMonthlyWTCount(cn, customerId);
                            break;
                            
                        case "0":
                            return;
                        default:
                            Console.WriteLine("Invalid selection");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(" error：" + ex.Message);
                }
            }
        }
    }

    // —— 展示我的账户 ——
    static async Task ShowAccounts(SqlConnection cn, int customerId)
    {
        await using var cmd = new SqlCommand(
            "SELECT AccountNumber, AccountType, Balance FROM dbo.Account WHERE CustomerID=@c ORDER BY AccountNumber", cn);
        cmd.Parameters.AddWithValue("@c", customerId);
        using var rd = await cmd.ExecuteReaderAsync();
        Console.WriteLine("\nAccountNumber  Type  Balance");
        while (await rd.ReadAsync())
            Console.WriteLine($"{rd.GetInt32(0),12}   {rd.GetString(1)}    {rd.GetDecimal(2):0.00}");
    }

    // —— 存款（D）——
    static async Task DepositFlow(SqlConnection cn, int customerId)
    {
        var acc = ReadInt("Deposit account number: ");
        var amount = ReadMoney("amount: ");

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

        var (type, bal, owner) = await GetAccountForUpdate(cn, tx, acc);
        if (owner != customerId) throw new InvalidOperationException("You cannot operate on accounts that are not yours");

        await ExecAsync(cn, tx,
            "UPDATE dbo.Account SET Balance = Balance + @amt WHERE AccountNumber=@a",
            ("@amt", amount), ("@a", acc));

        await ExecAsync(cn, tx,
            "INSERT INTO dbo.[Transaction](TransactionType,AccountNumber,DestinationAccountNumber,Amount,Comment,TransactionTimeUtc) " +
            "VALUES ('D', @a, NULL, @amt, @cmt, SYSUTCDATETIME())",
            ("@a", acc), ("@amt", amount), ("@cmt", (object?)"deposit" ?? DBNull.Value));

        await tx.CommitAsync();
        Console.WriteLine(" Deposit successful");
    }

    // —— 取款（W）——
    static async Task WithdrawFlow(SqlConnection cn, int customerId)
{
    var acc = ReadInt("Withdrawal account number: ");
    var amount = ReadMoney("amount: ");

    await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

    var (type, bal, owner) = await GetAccountForUpdate(cn, tx, acc);
    if (owner != customerId) throw new InvalidOperationException("You cannot operate on accounts that are not yours");

    // 新规则：任何账户都不允许负数
    var newBal = bal - amount;
    if (newBal < 0) throw new InvalidOperationException("Insufficient balance: The operation will result in a negative balance");

    await ExecAsync(cn, tx,
        "UPDATE dbo.Account SET Balance = @nb WHERE AccountNumber=@a",
        ("@nb", newBal), ("@a", acc));

    await ExecAsync(cn, tx,
        "INSERT INTO dbo.[Transaction](TransactionType,AccountNumber,DestinationAccountNumber,Amount,Comment,TransactionTimeUtc) " +
        "VALUES ('W', @a, NULL, @amt, @cmt, SYSUTCDATETIME())",
        ("@a", acc), ("@amt", amount), ("@cmt", (object?)"withdraw" ?? DBNull.Value));

    // —— 免费次数与手续费（W 第3次起收 $0.01），仍需确保不为负 ——
    var wtCount = await GetMonthlyWTCountAsync(cn, tx, acc);
    if (wtCount >= 3)
    {
        const decimal withdrawFee = 0.01m;
        var afterFee = newBal - withdrawFee;
        if (afterFee < 0) throw new InvalidOperationException("Insufficient balance after deducting handling fees: will be a negative number");

        await MaybeChargeFeeAsync(cn, tx, acc, withdrawFee, "withdraw fee");
    }

    await tx.CommitAsync();
    Console.WriteLine(" Withdrawal successful");
}

    // —— 转账（T）——
    static async Task TransferFlow(SqlConnection cn, int customerId)
    {
        var from = ReadInt("Transferring account number: ");
        var to   = ReadInt("Transfer account number: ");
        if (from == to) throw new InvalidOperationException("The transfer-out and transfer-in accounts cannot be the same");

        var amount = ReadMoney("amount: ");

        // 为避免死锁，按账户号升序加锁
        int first = Math.Min(from, to), second = Math.Max(from, to);

        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();

        var acc1 = await GetAccountForUpdate(cn, tx, first);
        var acc2 = await GetAccountForUpdate(cn, tx, second);

        var (typeFrom, balFrom, ownerFrom) = (from == first) ? acc1 : acc2;
        var (typeTo,   balTo,   ownerTo)   = (to   == first) ? acc1 : acc2;

        if (ownerFrom != customerId) throw new InvalidOperationException("The account being transferred does not belong to the current customer");

       // 本次是否会收转账手续费（第3次起，对来源账户）
    var already = await GetMonthlyWTCountAsync(cn, tx, from);
    var willCharge = (already + 1) >= 3;
    var fee = willCharge ? 0.05m : 0m;

    // 一次性校验：来源账户必须 ≥ 金额 + 可能手续费
    if (balFrom < amount + fee) throw new InvalidOperationException("Insufficient balance: The balance will be negative after deducting the handling fee");

    // 更新余额
    await ExecAsync(cn, tx, "UPDATE dbo.Account SET Balance = Balance - @amt WHERE AccountNumber=@a",
        ("@amt", amount), ("@a", from));
    await ExecAsync(cn, tx, "UPDATE dbo.Account SET Balance = Balance + @amt WHERE AccountNumber=@a",
        ("@amt", amount), ("@a", to));

    // 记录双边 T 交易
    await ExecAsync(cn, tx,
        "INSERT INTO dbo.[Transaction](TransactionType,AccountNumber,DestinationAccountNumber,Amount,Comment,TransactionTimeUtc) " +
        "VALUES ('T', @src, @dst, @amt, @cmt, SYSUTCDATETIME())",
        ("@src", from), ("@dst", to), ("@amt", amount), ("@cmt", (object?)"transfer out" ?? DBNull.Value));

    await ExecAsync(cn, tx,
        "INSERT INTO dbo.[Transaction](TransactionType,AccountNumber,DestinationAccountNumber,Amount,Comment,TransactionTimeUtc) " +
        "VALUES ('T', @dst, @src, @amt, @cmt, SYSUTCDATETIME())",
        ("@dst", to), ("@src", from), ("@amt", amount), ("@cmt", (object?)"transfer in" ?? DBNull.Value));

    // 若要收费，再追加 S（此时余额仍 ≥ 0）
    if (willCharge)
        await MaybeChargeFeeAsync(cn, tx, from, 0.05m, "transfer fee");

    await tx.CommitAsync();
        Console.WriteLine(" The transfer was successful");
    }

    // 统计某账户当月（UTC）已发生的 W/T 次数（用于免费次数判断）
static async Task<int> GetMonthlyWTCountAsync(SqlConnection cn, SqlTransaction tx, int accountNumber)
{
    var sql = @"
        SELECT COUNT(*)
        FROM dbo.[Transaction]
        WHERE AccountNumber = @a
          AND TransactionType IN ('W','T')
          AND DATEFROMPARTS(YEAR(TransactionTimeUtc), MONTH(TransactionTimeUtc), 1)
              = DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1);";
    await using var cmd = new SqlCommand(sql, cn, tx);
    cmd.Parameters.AddWithValue("@a", accountNumber);
    var c = (int)await cmd.ExecuteScalarAsync();
    return c;
}

// 若超过免费次数则追加 S 手续费（金额为正数，余额相应减少）
static async Task MaybeChargeFeeAsync(SqlConnection cn, SqlTransaction tx, int accountNumber, decimal fee, string reason)
{
    // 扣费：余额减少
    await ExecAsync(cn, tx, "UPDATE dbo.Account SET Balance = Balance - @fee WHERE AccountNumber=@a",
        ("@fee", fee), ("@a", accountNumber));

    await ExecAsync(cn, tx,
        "INSERT INTO dbo.[Transaction](TransactionType,AccountNumber,DestinationAccountNumber,Amount,Comment,TransactionTimeUtc) " +
        "VALUES ('S', @a, NULL, @amt, @cmt, SYSUTCDATETIME())",
        ("@a", accountNumber), ("@amt", fee), ("@cmt", (object?)reason ?? DBNull.Value));
}

// 菜单项：查看当前客户每个账户当月 W/T 次数
static async Task ShowMonthlyWTCount(SqlConnection cn, int customerId)
{
    Console.WriteLine("\nNumber of（UTC）W/T this mouth");
    await using var cmd = new SqlCommand(
        "SELECT AccountNumber FROM dbo.Account WHERE CustomerID=@c ORDER BY AccountNumber", cn);
    cmd.Parameters.AddWithValue("@c", customerId);
    using var rd = await cmd.ExecuteReaderAsync();
    var accounts = new List<int>();
    while (await rd.ReadAsync()) accounts.Add(rd.GetInt32(0));
    rd.Close();

    foreach (var a in accounts)
    {
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync();
        var cnt = await GetMonthlyWTCountAsync(cn, tx, a);
        await tx.CommitAsync();
        Console.WriteLine($"#{a}: {cnt} time");
    }
}


    // —— 工具：锁行读取账户（事务内保持锁）——
    static async Task<(char type, decimal bal, int owner)> GetAccountForUpdate(SqlConnection cn, SqlTransaction tx, int accountNumber)
    {
        await using var cmd = new SqlCommand(
            "SELECT AccountType, Balance, CustomerID FROM dbo.Account WITH (UPDLOCK, ROWLOCK) WHERE AccountNumber=@a", cn, tx);
        cmd.Parameters.AddWithValue("@a", accountNumber);
        using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) throw new InvalidOperationException($"account does not exist: {accountNumber}");
        return (rd.GetString(0)[0], rd.GetDecimal(1), rd.GetInt32(2));
    }

    static async Task ExecAsync(SqlConnection cn, SqlTransaction tx, string sql, params (string name, object value)[] p)
    {
        await using var cmd = new SqlCommand(sql, cn, tx);
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    // —— 输入辅助 —— 
    static string ReadHidden()
    {
        var buf = new List<char>(); ConsoleKeyInfo k;
        while ((k = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        { if (k.Key == ConsoleKey.Backspace && buf.Count > 0) { buf.RemoveAt(buf.Count - 1); Console.Write("\b \b"); }
          else if (!char.IsControl(k.KeyChar)) { buf.Add(k.KeyChar); Console.Write('*'); } }
        Console.WriteLine(); return new string(buf.ToArray());
    }

    static int ReadInt(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            if (int.TryParse(Console.ReadLine(), out var v)) return v;
            Console.WriteLine("Please enter an integer");
        }
    }

    static decimal ReadMoney(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            var s = Console.ReadLine();
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0) return v;
            Console.WriteLine("Please enter an amount > 0 (e.g. 100 or 100.50）。");
        }
    }
}
