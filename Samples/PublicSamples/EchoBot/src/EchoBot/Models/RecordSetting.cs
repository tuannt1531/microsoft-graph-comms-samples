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
    public class RecordSetting
    {
        public string? MeetingId { get; set; }

        /// <summary>
        /// Record bot setup
        /// </summary>
        /// <value>Record setup.</value>
        public string? Record { get; set; } = false;
    }
}

