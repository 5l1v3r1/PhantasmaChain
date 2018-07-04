using System;
using System.Collections.Generic;
using Phantasma.Utils;

//Some code parts taked from lidgren-network-gen3

namespace Phantasma.Network
{
    public interface INatPunchListener
    {
        void OnNatIntroductionRequest(Endpoint localEndPoint, Endpoint remoteEndPoint, string token);
        void OnNatIntroductionSuccess(Endpoint targetEndPoint, string token);
    }

    public class EventBasedNatPunchListener : INatPunchListener
    {
        public delegate void OnNatIntroductionRequest(Endpoint localEndPoint, Endpoint remoteEndPoint, string token);
        public delegate void OnNatIntroductionSuccess(Endpoint targetEndPoint, string token);

        public event OnNatIntroductionRequest NatIntroductionRequest;
        public event OnNatIntroductionSuccess NatIntroductionSuccess;

        void INatPunchListener.OnNatIntroductionRequest(Endpoint localEndPoint, Endpoint remoteEndPoint, string token)
        {
            if(NatIntroductionRequest != null)
                NatIntroductionRequest(localEndPoint, remoteEndPoint, token);
        }

        void INatPunchListener.OnNatIntroductionSuccess(Endpoint targetEndPoint, string token)
        {
            if (NatIntroductionSuccess != null)
                NatIntroductionSuccess(targetEndPoint, token);
        }
    }

    public sealed class NatPunchModule
    {
        struct RequestEventData
        {
            public Endpoint LocalEndPoint;
            public Endpoint RemoteEndPoint;
            public string Token;
        }

        struct SuccessEventData
        {
            public Endpoint TargetEndPoint;
            public string Token;
        }

        private readonly NetManager _netBase;
        private readonly Queue<RequestEventData> _requestEvents;
        private readonly Queue<SuccessEventData> _successEvents; 
        private const byte HostByte = 1;
        private const byte ClientByte = 0;
        public const int MaxTokenLength = 256;

        private INatPunchListener _natPunchListener;

        internal NatPunchModule(NetManager netBase, NetSocket socket)
        {
            _netBase = netBase;
            _requestEvents = new Queue<RequestEventData>();
            _successEvents = new Queue<SuccessEventData>();
        }

        public void Init(INatPunchListener listener)
        {
            _natPunchListener = listener;
        }

        public void NatIntroduce(
            Endpoint hostInternal,
            Endpoint hostExternal,
            Endpoint clientInternal,
            Endpoint clientExternal,
            string additionalInfo)
        {
            NetDataWriter dw = new NetDataWriter();

            //First packet (server)
            //send to client
            dw.Put(ClientByte);
            dw.Put(hostInternal);
            dw.Put(hostExternal);
            dw.Put(additionalInfo, MaxTokenLength);

            _netBase.SendRaw(Packet.CreateRawPacket(PacketProperty.NatIntroduction, dw), clientExternal);

            //Second packet (client)
            //send to server
            dw.Reset();
            dw.Put(HostByte);
            dw.Put(clientInternal);
            dw.Put(clientExternal);
            dw.Put(additionalInfo, MaxTokenLength);

            _netBase.SendRaw(Packet.CreateRawPacket(PacketProperty.NatIntroduction, dw), hostExternal);
        }

        public void PollEvents()
        {
            if (_natPunchListener == null)
                return;
            lock (_successEvents)
            {
                while (_successEvents.Count > 0)
                {
                    var evt = _successEvents.Dequeue();
                    _natPunchListener.OnNatIntroductionSuccess(evt.TargetEndPoint, evt.Token);
                }
            }
            lock (_requestEvents)
            {
                while (_requestEvents.Count > 0)
                {
                    var evt = _requestEvents.Dequeue();
                    _natPunchListener.OnNatIntroductionRequest(evt.LocalEndPoint, evt.RemoteEndPoint, evt.Token);
                }
            }
        }

        public void SendNatIntroduceRequest(Endpoint masterServerEndPoint, string additionalInfo)
        {
            if (!_netBase.IsRunning)
                return;

            //prepare outgoing data
            NetDataWriter dw = new NetDataWriter();
            string networkIp = NetUtils.GetLocalIp(true);
            int networkPort = _netBase.LocalEndPoint.Port;
            Endpoint localEndPoint = new Endpoint(networkIp, networkPort);
            dw.Put(localEndPoint);
            dw.Put(additionalInfo, MaxTokenLength);

            //prepare packet
            _netBase.SendRaw(Packet.CreateRawPacket(PacketProperty.NatIntroductionRequest, dw), masterServerEndPoint);
        }

        private void HandleNatPunch(Endpoint senderEndPoint, NetDataReader dr)
        {
            byte fromHostByte = dr.GetByte();
            if (fromHostByte != HostByte && fromHostByte != ClientByte)
            {
                //garbage
                return;
            }

            //Read info
            string additionalInfo = dr.GetString(MaxTokenLength);
            NetUtils.DebugWrite("[NAT] punch received from {0} - additional info: {1}", senderEndPoint, additionalInfo);

            //Release punch success to client; enabling him to Connect() to msg.Sender if token is ok
            lock (_successEvents)
            {
                _successEvents.Enqueue(new SuccessEventData { TargetEndPoint = senderEndPoint, Token = additionalInfo });
            }
        }

        private void HandleNatIntroduction(NetDataReader dr)
        {
            // read intro
            byte hostByte = dr.GetByte();
            Endpoint remoteInternal = dr.GetEndPoint();
            Endpoint remoteExternal = dr.GetEndPoint();
            string token = dr.GetString(MaxTokenLength);

            NetUtils.DebugWrite("[NAT] introduction received; we are designated " + (hostByte == HostByte ? "host" : "client"));
            NetDataWriter writer = new NetDataWriter();

            // send internal punch
            writer.Put(hostByte);
            writer.Put(token);
            _netBase.SendRaw(Packet.CreateRawPacket(PacketProperty.NatPunchMessage, writer), remoteInternal);
            NetUtils.DebugWrite("[NAT] internal punch sent to " + remoteInternal);

            // send external punch
            writer.Reset();
            writer.Put(hostByte);
            writer.Put(token);
            _netBase.SendRaw(Packet.CreateRawPacket(PacketProperty.NatPunchMessage, writer), remoteExternal);
            NetUtils.DebugWrite("[NAT] external punch sent to " + remoteExternal);
        }

        private void HandleNatIntroductionRequest(Endpoint senderEndPoint, NetDataReader dr)
        {
            Endpoint localEp = dr.GetEndPoint();
            string token = dr.GetString(MaxTokenLength);
            lock (_requestEvents)
            {
                _requestEvents.Enqueue(new RequestEventData
                {
                    LocalEndPoint = localEp,
                    RemoteEndPoint = senderEndPoint,
                    Token = token
                });
            }
        }

        internal void ProcessMessage(Endpoint senderEndPoint, PacketProperty property, byte[] data)
        {
            NetDataReader dr = new NetDataReader(data);

            switch (property)
            {
                case PacketProperty.NatIntroductionRequest:
                    //We got request and must introduce
                    HandleNatIntroductionRequest(senderEndPoint, dr);
                    break;
                case PacketProperty.NatIntroduction:
                    //We got introduce and must punch
                    HandleNatIntroduction(dr);
                    break;
                case PacketProperty.NatPunchMessage:
                    //We got punch and can connect
                    HandleNatPunch(senderEndPoint, dr);
                    break;
            }
        }
    }
}
