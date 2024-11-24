using System;
using System.IO;

namespace EchoBot.Media
{
    public class LoggingService
    {
        private readonly string _logFilePath;

        public LoggingService(string logFilePath = "logfile.txt")
        {
            _logFilePath = logFilePath;
        }

        public void Log(string message)
        {
            string logMessage = $"{message}";

            try
            {
                // Append log message to the file
                using (StreamWriter writer = new StreamWriter(_logFilePath, append: true))
                {
                    writer.WriteLine(logMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write to log file: {ex.Message}");
            }
        }
    }
}
