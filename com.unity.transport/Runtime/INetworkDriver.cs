using System;
using System.Net;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.Networking.Transport
{
    /// <summary>
    /// The NetworkDriver interface is the main entry point for the transport.
    /// The Driver is similar to a UDP socket which can handle many connections.
    /// </summary>
    public interface INetworkDriver : IDisposable
    {
		bool IsCreated { get; }

		// :: Driver Helpers
		/// <summary>
		/// Schedule a job to update the state of the NetworkDriver, read messages and events from the underlying
		/// network interface and populate the event queues to allow reading from connections concurrently.
		/// </summary>
		/// <param name="dep">
		/// Used to chain dependencies for jobs.
		/// </param>
		/// <returns>
		/// A <see cref="JobHandle"/> for the ScheduleUpdate Job.
		/// </returns>
		JobHandle ScheduleUpdate(JobHandle dep = default(JobHandle));

        /// <summary>
        /// Enable listening for incoming connections on this driver. Before calling this
        /// all connection attempts will be rejected.
        /// </summary>
        /// <returns>
        /// Returns 0 on Success.
        /// </returns>
        int Listen();

        /// <summary>
        /// Accept a pending connection attempt and get the established connection.
        /// This should be called until it returns an invalid connection to make sure
        /// all connections are accepted.
        /// </summary>
        /// <returns>
        /// Returns a newly created NetworkConnection if it was Successful and a default(NetworkConnection)
        /// if there where no more new NetworkConnections to accept.
        /// </returns>
        NetworkConnection Accept();

        /// <summary>
        /// Disconnect an existing connection.
        /// </summary>
        /// <returns>
        /// Returns 0 on Success.
        /// </returns>
        int Disconnect(NetworkConnection con);

        /// <summary>
        /// Get the state of an existing connection. If called with an invalid connection the call will return the Destroyed state.
        /// </summary>
        NetworkConnection.State GetConnectionState(NetworkConnection con);

        // :: Events
        /// <summary>
        /// Send a message to the specific connection.
        /// </summary>
        /// <param name="con">
        /// A NetworkConnection to the endpoint you want to send to.
        /// </param>
        /// <param name="strm">
        /// A valid DataStreamWriter.
        /// </param>
        /// <returns>
        /// Returns the size in bytes that was sent, -1 on failure.
        /// </returns>
        int Send(NetworkConnection con, DataStreamWriter strm);

        /// <summary>
        /// Send a message to the specific connection.
        /// </summary>
        int Send(NetworkConnection con, IntPtr data, int len);

        /// <summary>
        /// Receive an event for any connection.
        /// </summary>
        NetworkEvent.Type PopEvent(out NetworkConnection con, out DataStreamReader bs);

        /// <summary>
        /// Receive an event for a specific connection. Should be called until it returns Empty, even if the socket is disconnected.
        /// </summary>
        NetworkEvent.Type PopEventForConnection(NetworkConnection con, out DataStreamReader bs);
    }
}