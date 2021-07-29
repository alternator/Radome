using System.Net;
using System.Net.Sockets;
using Unity.Collections;
using UnityEngine;
using Unity.Networking.Transport;
using System.Threading;
using System.Threading.Tasks;
using ICKX.Radome;

public class LocalNetworkDiscovery : MonoBehaviour
{

	[SerializeField]
	int m_broadcastPort = 47777;

	[SerializeField]
	int m_broadcastKey = 2222;

	[SerializeField]
	int m_broadcastVersion = 1;

	[SerializeField]
	float m_broadcastInterval = 1.0f;

	[SerializeField]
	string m_broadcastData = "HELLO";
	[SerializeField]
	private bool autoStartAsHost = false;
	[SerializeField]
	private bool autoStartAsClient = false;

	private UdpClient client;
	private NetworkConnection connection;

	private byte[] discovetyPacket;

	public bool isStarted { get; private set; }
	public bool isHost { get; private set; }

	public int broadcastPort { get { return m_broadcastPort; } set { m_broadcastPort = value; } }
	public int broadcastKey { get { return m_broadcastKey; } set { m_broadcastKey = value; } }
	public int broadcastVersion { get { return m_broadcastVersion; } set { m_broadcastVersion = value; } }
	public float broadcastInterval { get { return m_broadcastInterval; } set { m_broadcastInterval = value; } }
	public string broadcastData { get { return m_broadcastData; } set { m_broadcastData = value; } }

	public delegate void OnReciveBroadcastEvent(IPEndPoint endPoint, int key, int version, string data);

	public event OnReciveBroadcastEvent OnReciveBroadcast = null;

	private void Awake()
	{

		if (autoStartAsHost)
		{
			var task = StartHost();
		}
		if (autoStartAsClient)
		{
			var task = StartClient();
		}
	}

	public void OnDestroy()
	{
		if (client == null) return;
		Stop();
	}

	public async Task StartHost()
	{
		if (isStarted) return;

		isStarted = true;
		isHost = true;

		var endpoint = new IPEndPoint(IPAddress.Broadcast, broadcastPort);
		client = new UdpClient();
		client.Connect(endpoint);
		Debug.Log("Broadcast StartHost : " + m_broadcastKey);

		unsafe
		{
			int strByteCount = NativeStreamWriter.GetByteSizeStr(m_broadcastData);

			using (var array = new NativeArray<byte>(10 + strByteCount, Allocator.Temp))
			{
				var writer = new NativeStreamWriter(array);
				writer.WriteInt(m_broadcastKey);
				writer.WriteInt(m_broadcastVersion);
				writer.WriteString(m_broadcastData);

				var reader = new NativeStreamReader(writer, 0, writer.Length);
				discovetyPacket = new byte[writer.Length];
				fixed (byte* ptr = discovetyPacket)
				{
					reader.ReadBytes(ptr, writer.Length);
				}
			}
		}

		while (true)
		{
			if (isStarted)
			{
				await Task.Delay((int)(1000 * m_broadcastInterval));
				await client.SendAsync(discovetyPacket, discovetyPacket.Length);
				//Debug.Log ("Broadcast SendAsync");
			}
			else
			{
				break;
			}
		}

		if (client != null)
		{
			client.Close();
			client = null;
		}
	}

	public async Task StartClient()
	{
		if (isStarted) return;

		isStarted = true;
		isHost = false;

		client = new UdpClient(broadcastPort);

		while (true)
		{
			if (isStarted)
			{
				Debug.Log("Broadcast StartClient");

				var result = await client.ReceiveAsync();
				var responseBytes = result.Buffer;

				if (responseBytes == null || responseBytes.Length == 0) continue;

				Debug.Log(string.Join(",", responseBytes));


				using (var array = new NativeArray<byte>(responseBytes.Length, Allocator.Temp))
				{
					var writer = new NativeStreamWriter(array);
					unsafe
					{
						fixed (byte* data = responseBytes)
						{
							writer.WriteBytes(data, responseBytes.Length);

							var reader = new NativeStreamReader(writer, 0, writer.Length);
							var key = reader.ReadInt();
							var version = reader.ReadInt();

							Debug.Log("OnReciveBroadcast key=" + key + ", version=" + version + "time=" + Time.time);
							if (key != m_broadcastKey) continue;
							if (version != m_broadcastVersion) continue;

							var str = reader.ReadString();

							Debug.Log("OnReciveBroadcast Succcess key=" + key + ", version=" + version + ", str=" + str + "time=" + Time.time);
							OnReciveBroadcast?.Invoke(result.RemoteEndPoint, key, version, str);
							//Stop ();
						}
					}
				}
			}
			else
			{
				break;
			}
		}

		if (client != null)
		{
			client.Close();
			client = null;
		}
	}

	public void Stop()
	{
		if (!isStarted) return;
		isStarted = false;

		if (client != null)
		{
			client.Close();
			client = null;
		}
	}
}
