using System;
using System.IO;
using System.Threading.Tasks;

namespace EchoBot.Media
{
    public class LoggingService
    {
        private readonly string _logFilePath;

        public LoggingService(string logFilePath = "logfile.txt")
        {
            _logFilePath = logFilePath;
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
