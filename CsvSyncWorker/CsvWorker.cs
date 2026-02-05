using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsvSyncWorker;

public class CsvWorker : BackgroundService
{
    private readonly ILogger<CsvWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly BackendClient _backendClient;

    public CsvWorker(
        ILogger<CsvWorker> logger,
        IConfiguration configuration,
        BackendClient backendClient)
    {
        _logger = logger;
        _configuration = configuration;
        _backendClient = backendClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var folderPath = _configuration["Watcher:FolderPath"];
        var intervalSeconds = int.TryParse(_configuration["Watcher:IntervalSeconds"], out var v) ? v : 60;

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            _logger.LogError("Watcher:FolderPath is not configured.");
            return;
        }

        _logger.LogInformation("CsvWorker started. Watching folder: {Folder} (every {Seconds} seconds)", folderPath, intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessFolderAsync(folderPath, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing folder.");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folderPath))
        {
            _logger.LogWarning("Folder does not exist: {Folder}", folderPath);
            return;
        }

        var csvFiles = Directory.GetFiles(folderPath, "*.csv");
        foreach (var file in csvFiles)
        {
            _logger.LogInformation("Processing CSV file: {File}", file);
            try
            {
                var records = await LoadCsvAsync(file, cancellationToken);
                await _backendClient.SyncUserDepartmentMappingsAsync(records, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process CSV file: {File}", file);
            }
        }
    }

    private async Task<List<UserDepartmentRecord>> LoadCsvAsync(string filePath, CancellationToken cancellationToken)
    {
        var result = new List<UserDepartmentRecord>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            Encoding = Encoding.UTF8
        };

        await using var reader = new StreamReader(filePath, Encoding.UTF8);
        await using var csv = new CsvReader(reader, config);

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = new UserDepartmentRecord
            {
                MicrosoftUsername = csv.GetField("microsoft_username"),
                Department = csv.GetField("department")
            };
            if (!string.IsNullOrWhiteSpace(record.MicrosoftUsername) &&
                !string.IsNullOrWhiteSpace(record.Department))
            {
                result.Add(record);
            }
        }

        return result;
    }
}

