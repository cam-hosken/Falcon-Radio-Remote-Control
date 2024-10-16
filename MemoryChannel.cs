using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PRC_138_Remote_Control
{
    public struct MemoryChannel
    {
        public int channelNumber { get; set; }
        public int txFrequency { get; set; }
        public int rxFrequency { get; set; }
        public FalconRadio.ModulationModes modulationMode { get; set; }
        public FalconRadio.Bandwidths bandwidth { get; set; }
        public FalconRadio.AGCSpeeds aGCMode { get; set; }
        public bool isRxOnly { get; set; }
        
        //public MemoryChannel()
        //{

        //}

    }

}
