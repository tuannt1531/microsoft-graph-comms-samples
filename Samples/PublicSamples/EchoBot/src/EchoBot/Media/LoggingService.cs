using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace EchoBot.Media
{
    public class LoggingService
    {
        private readonly string _logFilePath;

        private string SanitizeFileName(string fileName)
        {
            // Replace invalid characters (:, /, \, ., etc.) with _
            return Regex.Replace(fileName, @"[:\/\\\.]", "_");
        }

        public LoggingService(string logFileName = "logfile.txt")
        {
            var logDirectory = "aaibotlogs";
            // Ensure the logs directory exists
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Set the full log file path
            logFileName = SanitizeFileName(logFileName);
            _logFilePath = Path.Combine(logDirectory, logFileName);
        }

        public async Task Log(string message)
        {
            string logMessage = $"{message}";

            try
            {
                // Append log message to the file
                using (StreamWriter writer = new StreamWriter(_logFilePath, append: true))
                {
                    await writer.WriteLineAsync(logMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write to log file: {ex.Message}");
            }
        }
    }
}
