using System.Collections.Generic;

namespace MQTTOperationsService.Configuration
{
    public class Operation
    {
        public string Name
        {
            get; set;
        }

        public Topics Topics
        {
            get; set;
        }

        public string Command
        {
            get; set;
        }

        public bool CaptureOutput
        {
            get; set;
        } = true;

        public List<Paramater> Parameters
        {
            get; set;
        }

        /// <summary>
        /// A time to live for the message in seconds.
        /// -1 means treat the operation as if it has no expiry
        /// </summary>
        public int MessageExpiryIntervalSecs
        {
            get; set;
        } = 30; //default to 30 seconds
    }
}
