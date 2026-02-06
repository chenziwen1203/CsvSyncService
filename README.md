## CsvSyncService - Windows Service for User-Department Mapping

cd /Users/chenziwen/PycharmProjects/Falcon/CsvSyncService/CsvSyncWorker

dotnet publish -c Release -r win-x64 --self-contained false -o ./publish

New-Service -Name "CsvSyncService" -BinaryPathName "C:\Source\AI Server\CsvSyncService\publish\CsvSyncWorker.exe" -StartupType Automatic

Start-Service CsvSyncService


###你需要怎么设置权限（本地目录）
在目标 Windows 机器上：
服务账号：services.msc → CsvSyncService → 属性 → Log On，建议用一个明确的本地用户/域用户（也可用 LocalSystem）。
目录 ACL：给该账号对 C:\CsvDropFolder（或你的目录）授予 Modify(M)（包含删除）权限。可用管理员命令：
icacls "C:\CsvDropFolder" /grant "YourUser:(OI)(CI)M"
