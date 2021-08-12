using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MQTTOperationsService.Configuration
{
    public class BrokerSettings
    {
        public string Hostname
        { 
            get; set;
        }

        public int Port
        { 
            get; set; 
        } = 8883;

        
    }
}
