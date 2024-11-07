// ***********************************************************************
// Assembly         : EchoBot.Models
//
// Last Modified By :
// Last Modified On :
// ***********************************************************************
// <summary></summary>
// ***********************************************************************
namespace EchoBot.Models
{
    /// <summary>
    /// The setting body.
    /// </summary>
    public class LanguageSetting
    {
        public string? MeetingId { get; set; }

        /// <summary>
        /// Gets or sets source language.
        /// </summary>
        /// <value>The source language.</value>
        public string? SourceLanguage { get; set; } = "vi"; // Default to Vietnamese

        /// <summary>
        /// Gets or sets translated language.
        /// </summary>
        /// <value>The target language.</value>
        public string? TargetLanguage { get; set; } = "en"; // Default to English
    }
}

