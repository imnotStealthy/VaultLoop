using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ReplayGlitchGTA;

/// <summary>
/// Reads the TCP connections owned by a process, so the effect of the managed firewall rule
/// can be observed instead of assumed. A rule that reports <c>Active</c> while the game still
/// holds an established connection inside the blocked set is not doing anything.
/// </summary>
/// <remarks>Read-only: this never opens, closes, or alters a connection.</remarks>
internal static class GameConnectionInspector
{
    private const int AddressFamilyInternetwork = 2;
    private const int AddressFamilyInternetworkV6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int InsufficientBuffer = 122;
    private const int NoError = 0;
    private const int InternetworkRowSize = 24;
    private const int InternetworkV6RowSize = 56;
    private const int MaximumResizeAttempts = 6;

    internal static IReadOnlyList<GameConnection> GetConnections(int processId)
    {
        var connections = new List<GameConnection>();
        ReadTable(AddressFamilyInternetwork, processId, connections);
        ReadTable(AddressFamilyInternetworkV6, processId, connections);
        return connections;
    }

    private static void ReadTable(
        int addressFamily, int processId, ICollection<GameConnection> connections)
    {
        var bufferSize = 0;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < MaximumResizeAttempts; attempt++)
            {
                var allocatedSize = bufferSize;
                var result = GetExtendedTcpTable(buffer, ref bufferSize, false,
                    addressFamily, TcpTableOwnerPidAll, 0);
                if (result == NoError && buffer != IntPtr.Zero)
                {
                    ReadRows(buffer, allocatedSize, addressFamily, processId, connections);
                    return;
                }
                if (result != InsufficientBuffer || bufferSize <= 0)
                {
                    return;
                }

                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
                buffer = Marshal.AllocHGlobal(bufferSize);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never take the application down; an unreadable table is
            // reported as "no connections" by the caller.
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static void ReadRows(
        IntPtr buffer, int bufferSize, int addressFamily, int processId,
        ICollection<GameConnection> connections)
    {
        var entryCount = Marshal.ReadInt32(buffer);
        var isVersion6 = addressFamily == AddressFamilyInternetworkV6;
        var rowSize = isVersion6 ? InternetworkV6RowSize : InternetworkRowSize;

        // The row count comes from the kernel and should always fit, but this walks unmanaged
        // memory by hand inside an elevated process: clamp it to what was actually allocated
        // rather than trusting the header.
        var capacity = (bufferSize - sizeof(int)) / rowSize;
        if (entryCount > capacity)
        {
            entryCount = capacity < 0 ? 0 : capacity;
        }

        for (var index = 0; index < entryCount; index++)
        {
            var row = IntPtr.Add(buffer, 4 + (index * rowSize));
            var owningProcessId = Marshal.ReadInt32(row, isVersion6 ? 52 : 20);
            if (owningProcessId != processId)
            {
                continue;
            }

            var addressBytes = new byte[isVersion6 ? 16 : 4];
            Marshal.Copy(IntPtr.Add(row, isVersion6 ? 24 : 12), addressBytes, 0,
                addressBytes.Length);
            var remotePort = ReadNetworkPort(row, isVersion6 ? 44 : 16);
            var localPort = ReadNetworkPort(row, isVersion6 ? 20 : 8);
            var state = (TcpConnectionState)Marshal.ReadInt32(row, isVersion6 ? 48 : 0);

            // A closed or listening row carries no meaningful remote endpoint.
            if (remotePort == 0 && state != TcpConnectionState.Established)
            {
                continue;
            }
            connections.Add(new GameConnection(
                new IPAddress(addressBytes), remotePort, localPort, state));
        }
    }

    /// <summary>Ports sit in the low half of a DWORD, in network byte order.</summary>
    private static int ReadNetworkPort(IntPtr row, int offset)
    {
        var raw = Marshal.ReadInt32(row, offset);
        return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable, ref int tableSize, bool sortOrder, int addressFamily,
        int tableClass, int reserved);
}

internal enum TcpConnectionState
{
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAcknowledgement = 10,
    TimeWait = 11,
    DeleteTcb = 12
}

internal sealed class GameConnection
{
    internal GameConnection(
        IPAddress remoteAddress, int remotePort, int localPort, TcpConnectionState state)
    {
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        LocalPort = localPort;
        State = state;
    }

    internal IPAddress RemoteAddress { get; }

    internal int RemotePort { get; }

    /// <summary>
    /// Identifies the connection. Adding a block rule does not tear down an already
    /// established flow, so "a connection to a blocked address exists" is not by itself
    /// evidence that the block failed. A connection whose local port appeared *after* the
    /// rule became active is, because the handshake had to complete through the rule.
    /// </summary>
    internal int LocalPort { get; }

    internal TcpConnectionState State { get; }

    internal bool IsVersion6 => RemoteAddress.AddressFamily == AddressFamily.InterNetworkV6;

    internal string Endpoint =>
        IsVersion6 ? $"[{RemoteAddress}]:{RemotePort}" : $"{RemoteAddress}:{RemotePort}";
}
