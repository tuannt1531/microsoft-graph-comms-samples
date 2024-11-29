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
        public bool Record { get; set; } = false;

        public string? UserId { get; set; }
    }
}

