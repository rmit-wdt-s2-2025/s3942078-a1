# s3942078-a1
Internet Banking – A1 (.NET 9 + Azure SQL)

## Environment
- .NET 9 SDK
- Azure SQL Database (Serverless, with public endpoint enabled and client IP added)
- Azure Data Studio / SSMS

## Initialization Steps
1. Connect to `wdtbank-db` in Azure Data Studio and execute `CreateTables.sql` to create the table.
2. Run a tool to generate a password hash (SimpleHashing.NET, PBKDF2 94-character).
3. Insert a login into the `Login` table: `(LoginID='12345678', CustomerID=1, PasswordHash=<HASH>)`.
4. (Optional) Create a database including users:
```sql
CREATE USER [appuser] WITH PASSWORD = '***';
ALTER ROLE db_datareader ADD MEMBER [appuser];
ALTER ROLE db_datawriter ADD MEMBER [appuser];
