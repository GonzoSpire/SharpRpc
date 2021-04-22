﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpRpc
{
    public class ClientCredentials
    {
        public static ClientCredentials None { get; } = new Null();

        internal virtual void OnBeforeLogin(ILoginMessage loginMsg) { }

        private class Null : ClientCredentials
        {
        }
    }
}
