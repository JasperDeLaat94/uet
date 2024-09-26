﻿namespace Redpoint.Uba
{
    using Redpoint.ProcessExecution;
    using System;

    /// <summary>
    /// Represents a UBA server, which can be used to run processes on remote machines.
    /// 
    /// The UBA networking model is inverted from what conventionally makes sense: the server runs on the machine that wants to run processes remotely, and the clients are the remote agents assisting with work.
    /// 
    /// We don't support the UBA mode whereby the server listens on a port, and remote agents (clients) initiated a connection back to the server. That's because most developer machines are behind a firewall that remote agents won't be able to initiate a connection through.
    /// 
    /// Instead, we only support the model by which the remote agents (clients) listen on a port, and the server actively connects to them using <see cref="AddRemoteAgent(string, int, string)"/>.
    /// </summary>
    public interface IUbaServer : IProcessExecutor, IAsyncDisposable
    {
        /// <summary>
        /// Add a remote agent (client) to the list of remote agents we'll try to use for running processes.
        /// </summary>
        /// <param name="ip">The IP address of the remote agent.</param>
        /// <param name="port">The port of the remote agent.</param>
        /// <param name="crypto">If the remote agent has the '-crypto' parameter set, this should be the 32 character hexadecimal value representing the symmetric cryptographic key.</param>
        /// <returns>True if the agent was successfully added.</returns>
        bool AddRemoteAgent(string ip, int port, string crypto = "");
    }
}