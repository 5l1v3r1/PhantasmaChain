﻿using Phantasma.Cryptography;
using Phantasma.Numerics;
using System;
using System.Collections.Generic;

namespace Phantasma.Blockchain.Contracts.Native
{
    public sealed class ConsensusContract : NativeContract
    {
        internal override ContractKind Kind => ContractKind.Custom;

        public ConsensusContract() : base()
        {
        }

        public bool IsValidMiner(Address address)
        {
            return true;
        }

        public bool IsValidReceiver(Address address)
        {
            return true;
        }
    }
}
