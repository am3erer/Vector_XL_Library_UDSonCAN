using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using vxlapi_NET;
using System.Threading;
using System.Runtime.InteropServices;


namespace ConsoleApp1
{
    public class CANTP
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int WaitForSingleObject(int handle, int timeOut);

        public struct DiagPDU
        {
            public byte[] data;
            public int dataLength;
        }
        private XLDriver CANTPDriver = new XLDriver();
        public uint TxID { set; get; }
        public uint RxID { set; get; }
        public byte RxSTmin { set; get; }
        public byte RxBlockSize { set; get; }
        public byte PaddingValue { set; get; }

        private byte TxdiagBlockSize, TxdiagSTmin;

        private int portHandle = -1;
        private int eventHandle = -1;
        private UInt64 accessMask = 0;
        private UInt64 permissionMask = 0;
        private UInt64 txMask = 0;

        //private XLDefine.XL_HardwareType hwType = XLDefine.XL_HardwareType.XL_HWTYPE_NONE;
        //private uint hwIndex = 0;
        //private uint hwChannel = 0;

        private Thread rxThread;
        private static bool blockRxThread = false;

        private int waitFrameType = 0;
        private EventWaitHandle waitforResponseEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        private EventWaitHandle waitforFlowControlEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        private EventWaitHandle waitforConsecutiveFrameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        public event EventHandler<DiagPDU> CanTpOnDiagPDURecedEvent;

        private byte requestService;

        public CANTP(uint txID, uint rxID, byte blockSize, byte stmin, byte paddingValue)
        {
            this.TxID = txID;
            this.RxID = rxID;
            this.RxBlockSize = blockSize;
            this.RxSTmin = stmin;
            this.PaddingValue = paddingValue;
        }

        public long CanTpCreateConnection(string appName, uint appChannel, XLDefine.XL_HardwareType hwType, uint hwIndex, uint hwChannel, XLDefine.XL_BusTypes busType)
        {
            XLDefine.XL_Status status;

            status = CANTPDriver.XL_OpenDriver();
            Console.WriteLine("Open Driver       : " + status);
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                return -1;
            }

            status = CANTPDriver.XL_SetApplConfig(appName, appChannel, hwType, hwIndex, hwChannel, busType);
            Console.WriteLine("SetApplConfig      : " + status);
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                return -2;
            }
            accessMask = CANTPDriver.XL_GetChannelMask(hwType, (int)hwIndex, (int)hwChannel);
            permissionMask = accessMask;
            txMask = accessMask;

            status = CANTPDriver.XL_OpenPort(ref portHandle, appName, accessMask, ref permissionMask, 1024, XLDefine.XL_InterfaceVersion.XL_INTERFACE_VERSION, XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN);
            Console.WriteLine("Open Port      : " + status);
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                return -3;
            }

            status = CANTPDriver.XL_CanRequestChipState(portHandle, accessMask);
            Console.WriteLine("Can Request Chip State: " + status);
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                return -4;
            }

            status = CANTPDriver.XL_ActivateChannel(portHandle, accessMask, XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN, XLDefine.XL_AC_Flags.XL_ACTIVATE_NONE);
            Console.WriteLine("Activate Channel      : " + status);
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                return -5;
            }

            status = CANTPDriver.XL_SetNotification(portHandle, ref eventHandle, 1);
            Console.WriteLine("Set Notification      : " + status);
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                return -6;
            }

            status = CANTPDriver.XL_ResetClock(portHandle);
            Console.WriteLine("Reset Clock           : " + status + "\n\n");
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                return -7;
            }

            Console.WriteLine("Start Rx thread...");
            rxThread = new Thread(new ThreadStart(CanTp_ReceptionInd));
            rxThread.Start();

            return 0;
        }


        public int CanTpSendData(byte[] data)
        {
            XLDefine.XL_Status txStatus;

            XLClass.xl_event_collection xlEventCollection = new XLClass.xl_event_collection(1);

            int dataLength = data.Length;
            if (dataLength <= 7)
            {
                CanTpSendSingleFrame(data);
                waitFrameType = 1;
            }
            else
            {
                CanTpSendFirstFrame(data);
                waitFrameType = 3;
                if (!waitforFlowControlEvent.WaitOne(1000))
                {
                    return -1;
                }
                int sn = 1;
                //FF中已经发送了6字节 offset从6开始 CF每次发送7字节
                if (this.TxdiagBlockSize == 0)
                {
                    for (int i = 6; i < dataLength; i += 7)
                    {
                        CanTpSendConsecutiveFrame(data, i, sn);
                        sn++;
                    }
                }
                else
                {
                    //offset是已发送长度 下次索引直接从offset开始
                    int offset = 6;
                    while (offset < dataLength)
                    {
                        int count;
                        for (count = 0; count < this.TxdiagBlockSize; count ++)
                        {
                            CanTpSendConsecutiveFrame(data,offset,sn);
                            if(count != this.TxdiagBlockSize-1)
                            {
                                Thread.Sleep(this.TxdiagSTmin);
                            }
                            offset += 7;
                            sn++;
                        }
                        waitFrameType = 3;
                        if (!waitforFlowControlEvent.WaitOne(1000))
                        {
                            return -1;
                        }
                    }
                }
            }
            return 0;
        }

        private void CanTpSendSingleFrame(byte[] data)
        {
            XLDefine.XL_Status txStatus;

            XLClass.xl_event_collection xlEventCollection = new XLClass.xl_event_collection(1);

            xlEventCollection.xlEvent[0].tagData.can_Msg.id = this.TxID;
            xlEventCollection.xlEvent[0].tagData.can_Msg.dlc = 8;
            xlEventCollection.xlEvent[0].tagData.can_Msg.data[0] = (byte)data.Length;
            for (int i = 0; i < data.Length; i++)
            {
                xlEventCollection.xlEvent[0].tagData.can_Msg.data[i + 1] = data[i];
            }
            for (int i = data.Length; i < 7; i++)
            {
                xlEventCollection.xlEvent[0].tagData.can_Msg.data[i+1] = this.PaddingValue;
            }
            xlEventCollection.xlEvent[0].tag = XLDefine.XL_EventTags.XL_TRANSMIT_MSG;
            txStatus = CANTPDriver.XL_CanTransmit(portHandle, accessMask, xlEventCollection);
            waitFrameType = 1;
            Console.WriteLine("Transmit Message      : " + txStatus);
        }

        private void CanTpSendFirstFrame(byte[] data)
        {
            int dataLength;
            XLDefine.XL_Status txStatus;

            XLClass.xl_event_collection xlEventCollection = new XLClass.xl_event_collection(1);

            dataLength = data.Length;
            xlEventCollection.xlEvent[0].tagData.can_Msg.id = this.TxID;
            xlEventCollection.xlEvent[0].tagData.can_Msg.dlc = 8;
            xlEventCollection.xlEvent[0].tagData.can_Msg.data[0] = (byte)(0x10 + (dataLength>>8));
            xlEventCollection.xlEvent[0].tagData.can_Msg.data[1] = (byte)(dataLength & 0xFF);
            for (int i = 0; i < 6; i++)
            {
                xlEventCollection.xlEvent[0].tagData.can_Msg.data[i + 2] = data[i];
            }
            xlEventCollection.xlEvent[0].tag = XLDefine.XL_EventTags.XL_TRANSMIT_MSG;
            txStatus = CANTPDriver.XL_CanTransmit(portHandle, accessMask, xlEventCollection);
        }

        private void CanTpSendConsecutiveFrame(byte[] data, int offset,int sequenceNum)
        {
            XLDefine.XL_Status txStatus;

            XLClass.xl_event_collection xlEventCollection = new XLClass.xl_event_collection(1);

            int dataLength;
            if (offset >= data.Length)
            {
                return;
            }
            else if (data.Length - offset >= 7)
            {
                dataLength = 7;
            }
            else
            {
                dataLength = data.Length - offset;
            }

            sequenceNum = sequenceNum % 16;
            xlEventCollection.xlEvent[0].tagData.can_Msg.id = this.TxID;
            xlEventCollection.xlEvent[0].tagData.can_Msg.dlc = 8;
            xlEventCollection.xlEvent[0].tagData.can_Msg.data[0] = (byte)(0x20 + sequenceNum);
            for (int i = 0; i < dataLength; i++)
            {
                xlEventCollection.xlEvent[0].tagData.can_Msg.data[i+1] = data[offset+i];
            }
            for (int i = dataLength; i < 7; i++)
            {
                xlEventCollection.xlEvent[0].tagData.can_Msg.data[dataLength + 1] = this.PaddingValue;
            }
            xlEventCollection.xlEvent[0].tag = XLDefine.XL_EventTags.XL_TRANSMIT_MSG;
            txStatus = CANTPDriver.XL_CanTransmit(portHandle, accessMask, xlEventCollection);
        }

        private void CanTpSendFlowControlFrame()
        {
            XLDefine.XL_Status txStatus;

            XLClass.xl_event_collection xlEventCollection = new XLClass.xl_event_collection(1);

            xlEventCollection.xlEvent[0].tagData.can_Msg.id = this.TxID;
            xlEventCollection.xlEvent[0].tagData.can_Msg.dlc = 8;
            xlEventCollection.xlEvent[0].tagData.can_Msg.data[0] = 0x30;
            xlEventCollection.xlEvent[0].tagData.can_Msg.data[1] = this.RxBlockSize;
            for (int i = 0; i < 5; i++)
            {
                xlEventCollection.xlEvent[0].tagData.can_Msg.data[i + 2] = this.PaddingValue;
            }
            xlEventCollection.xlEvent[0].tag = XLDefine.XL_EventTags.XL_TRANSMIT_MSG;
            txStatus = CANTPDriver.XL_CanTransmit(portHandle, accessMask, xlEventCollection);
        }


        public void CanTp_ReceptionInd()
        {
            // Create new object containing received data 
            XLClass.xl_event receivedEvent = new XLClass.xl_event();

            // Result of XL Driver function calls
            XLDefine.XL_Status xlStatus = XLDefine.XL_Status.XL_SUCCESS;

            // Result values of WaitForSingleObject 
            XLDefine.WaitResults waitResult = new XLDefine.WaitResults();


            byte diagFrameType;
            byte[] diagPDU = new byte[1024];
            int diagPDULength=0,diagPDUoffset=0;
            byte diagSequenceNum = 0;
            byte diagFlowState;
            // Note: this thread will be destroyed by MAIN

            while (true)
            {
                // Wait for hardware events
                waitResult = (XLDefine.WaitResults)WaitForSingleObject(eventHandle, 1000);
                if (waitFrameType == 0)
                {
                    continue;
                }

                // If event occurred...
                if (waitResult != XLDefine.WaitResults.WAIT_TIMEOUT)
                {
                    // ...init xlStatus first
                    xlStatus = XLDefine.XL_Status.XL_SUCCESS;

                    // afterwards: while hw queue is not empty...
                    while (xlStatus != XLDefine.XL_Status.XL_ERR_QUEUE_IS_EMPTY)
                    {
                        // ...block RX thread to generate RX-Queue overflows
                        while (blockRxThread) Thread.Sleep(1000);

                        // ...receive data from hardware.
                        xlStatus = CANTPDriver.XL_Receive(portHandle, ref receivedEvent);

                        //  If receiving succeed....
                        if (xlStatus == XLDefine.XL_Status.XL_SUCCESS)
                        {
                            Console.WriteLine(receivedEvent.tagData.can_Msg.id);
                            if ((receivedEvent.flags & XLDefine.XL_MessageFlags.XL_EVENT_FLAG_OVERRUN) != 0)
                            {
                                Console.WriteLine("-- XL_EVENT_FLAG_OVERRUN --");
                            }

                            // ...and data is a Rx msg...
                            if (receivedEvent.tag == XLDefine.XL_EventTags.XL_RECEIVE_MSG)
                            {
                                if (receivedEvent.tagData.can_Msg.id == this.RxID)
                                {
                                    Console.WriteLine(CANTPDriver.XL_GetEventString(receivedEvent));
                                    diagFrameType = (byte)(receivedEvent.tagData.can_Msg.data[0] >> 4);

                                    switch (diagFrameType)
                                    {
                                        //Single Frame
                                        case 0:
                                            if (waitFrameType == 1)
                                            {
                                                diagPDULength = receivedEvent.tagData.can_Msg.data[0] & 0x0F;
                                                for (int i = 0; i < diagPDULength; i++)
                                                {
                                                    diagPDU[i] = receivedEvent.tagData.can_Msg.data[i + 1];
                                                }
                                                OnDiagPduReceived(diagPDU, diagPDULength);
                                                Console.WriteLine("PDU Length:" + diagPDULength);
                                            }
                                            break;
                                        //FirstFrame
                                        case 1:
                                            if (waitFrameType == 1)
                                            {
                                                diagPDULength = (receivedEvent.tagData.can_Msg.data[0] & 0x0F) * 256 + (receivedEvent.tagData.can_Msg.data[1]);
                                                for (int i = 0; i < 6; i++)
                                                {
                                                    diagPDU[i] = receivedEvent.tagData.can_Msg.data[i + 2];
                                                }
                                                diagPDUoffset = 6;
                                                diagSequenceNum = 0;
                                                CanTpSendFlowControlFrame();
                                                waitFrameType = 2;
                                                Console.WriteLine("PDU Length:" + diagPDULength);
                                            }
                                            break;
                                        //ConsecutiveFrame
                                        case 2:
                                            if (waitFrameType == 2)
                                            {
                                                if (diagSequenceNum + 1 == (byte)(receivedEvent.tagData.can_Msg.data[0] & 0x0F))
                                                {
                                                    diagSequenceNum = (byte)(receivedEvent.tagData.can_Msg.data[0] & 0x0F);
                                                    for (int i = diagPDUoffset; i < diagPDUoffset + 7; i++)
                                                    {
                                                        diagPDU[i] = receivedEvent.tagData.can_Msg.data[i + 1];
                                                    }
                                                    if (diagPDUoffset == diagPDULength)
                                                    {
                                                        OnDiagPduReceived(diagPDU,diagPDULength);
                                                    }
                                                }
                                            }
                                            break;
                                        //FlowControl
                                        case 3:
                                            if (waitFrameType == 3)
                                            {
                                                diagFlowState = (byte)(receivedEvent.tagData.can_Msg.data[0] & 0x0F);
                                                this.TxdiagBlockSize = receivedEvent.tagData.can_Msg.data[1];
                                                this.TxdiagSTmin = receivedEvent.tagData.can_Msg.data[2];
                                                waitforFlowControlEvent.Set();
                                            }
                                            break;

                                    }

                                }


                                //if ((receivedEvent.tagData.can_Msg.flags & XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_OVERRUN) != 0)
                                //{
                                //    Console.WriteLine("-- XL_CAN_MSG_FLAG_OVERRUN --");
                                //}

                                //// ...check various flags
                                //if ((receivedEvent.tagData.can_Msg.flags & XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_ERROR_FRAME)
                                //    == XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_ERROR_FRAME)
                                //{
                                //    Console.WriteLine("ERROR FRAME");
                                //}

                                //else if ((receivedEvent.tagData.can_Msg.flags & XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_REMOTE_FRAME)
                                //    == XLDefine.XL_MessageFlags.XL_CAN_MSG_FLAG_REMOTE_FRAME)
                                //{
                                //    Console.WriteLine("REMOTE FRAME");
                                //}

                                //else
                                //{
                                //    Console.WriteLine(CANTPDriver.XL_GetEventString(receivedEvent));
                                //}
                            }
                        }
                    }
                }
                // No event occurred
            }
        }

        private void OnDiagPduReceived(byte[] diagPDU,int diagPDULength)
        {
            DiagPDU diagPDUArgs;
            diagPDUArgs.data = diagPDU;
            diagPDUArgs.dataLength = diagPDULength;
            CanTpOnDiagPDURecedEvent(this,diagPDUArgs);
        }

        //public void CANTransmitDemo()
        //{
        //    XLDefine.XL_Status txStatus;

        //    // Create an event collection with 2 messages (events)
        //    XLClass.xl_event_collection xlEventCollection = new XLClass.xl_event_collection(2);

        //    // event 1
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.id = 0x100;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.dlc = 8;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[0] = 1;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[1] = 2;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[2] = 3;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[3] = 4;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[4] = 5;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[5] = 6;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[6] = 7;
        //    xlEventCollection.xlEvent[0].tagData.can_Msg.data[7] = 8;
        //    xlEventCollection.xlEvent[0].tag = XLDefine.XL_EventTags.XL_TRANSMIT_MSG;

        //    // event 2
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.id = 0x200;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.dlc = 8;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[0] = 9;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[1] = 10;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[2] = 11;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[3] = 12;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[4] = 13;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[5] = 14;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[6] = 15;
        //    xlEventCollection.xlEvent[1].tagData.can_Msg.data[7] = 16;
        //    xlEventCollection.xlEvent[1].tag = XLDefine.XL_EventTags.XL_TRANSMIT_MSG;


        //    // Transmit events
        //    txStatus = CANTPDriver.XL_CanTransmit(portHandle, txMask, xlEventCollection);
        //    Console.WriteLine("Transmit Message      : " + txStatus);
        //}
    }
}
