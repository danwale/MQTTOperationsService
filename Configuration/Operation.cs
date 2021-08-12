using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    }
}
