using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vxlapi_NET;

namespace ConsoleApp1
{
    class DiagCommonClass
    {
        private CANTP CANTPHandle;
        private byte diagReqService;
        public CANTP.DiagPDU receivedDiagPDU;
        private EventWaitHandle[] diagResponseReceivedEvent = new EventWaitHandle[2];
        private EventWaitHandle diagNormalReceivedEvent = new EventWaitHandle(false,EventResetMode.AutoReset);
        private EventWaitHandle diagPaddingReceivedEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        public DiagCommonClass(uint txID, uint rxID, byte blockSize, byte stmin, byte paddingValue,
            string appName, uint appChannel, XLDefine.XL_HardwareType hwType, uint hwIndex, uint hwChannel, XLDefine.XL_BusTypes busType)
        {
            CANTPHandle = new CANTP(txID, rxID, blockSize, stmin, paddingValue);
            CANTPHandle.CanTpCreateConnection(appName, 0, XLDefine.XL_HardwareType.XL_HWTYPE_VIRTUAL, 0, 0, XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN);
            CANTPHandle.CanTpOnDiagPDURecedEvent += diagResponseReceived;
            diagResponseReceivedEvent[0] = diagNormalReceivedEvent;
            diagResponseReceivedEvent[1] = diagPaddingReceivedEvent;
        }

        public int ReadDataByID(byte[] dataIdentifier)
        {
            int result;
            byte[] data= { 0x22, 0x00, 0x00 };
            for(int i=0;i<2;i++)
            {
                data[i + 1] = dataIdentifier[i];
            }
            diagReqService = 0x22;
            CANTPHandle.CanTpSendData(data);
            result = waitDiagResponse();
            if (result == -1)
            {
                //Timeout
                return -1;
            }
            else
            {
                return 0;
            }
        }

        private int waitDiagResponse()
        {
            int result;
            //WaitAny 已完成的任务在 tasks 数组参数中的索引，如果发生超时，则为 -1。
            //TODO          P2  P2*
            result = EventWaitHandle.WaitAny(diagResponseReceivedEvent, 500);
            if (result == 1)
            {
                while (result == 1)
                {
                    result = EventWaitHandle.WaitAny(diagResponseReceivedEvent, 1000);
                }
            }
            if (result == -1)
            {
                return -1;
            }
            else
            {
                return 0;
            }
        }

        private void diagResponseReceived(object sender, CANTP.DiagPDU diagPDU)
        {
            if (diagPDU.data[0] == 0x7F && diagPDU.data[1] == diagReqService && diagPDU.data[2] == 0x78)
            {
                diagPaddingReceivedEvent.Set();
            }
            else if (diagPDU.data[0] == diagReqService + 0x40
                || (diagPDU.data[0] == 0x7F && diagPDU.data[1] == diagReqService))
            {
                receivedDiagPDU.data = diagPDU.data;
                receivedDiagPDU.dataLength = diagPDU.dataLength;
                diagNormalReceivedEvent.Set();
            }
            else
            {
                return;
            }
        }
    }
}
