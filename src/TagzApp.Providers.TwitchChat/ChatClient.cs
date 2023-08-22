using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TagzApp.Providers.TwitchChat;


public partial class ChatClient : IChatClient
{

	public const string LOGGER_CATEGORY = "Providers.TwitchChat";
	private TcpClient? _TcpClient;
	private StreamReader? _InputStream;
	private StreamWriter? _OutputStream;

	[GeneratedRegex("!([^@]+)@")]
	private static partial Regex UserNameRegEx();

	[GeneratedRegex("badges=([^;]*)")]
	private static partial Regex BadgesRegEx();

	[GeneratedRegex("display-name=([^;]*)")]
	private static partial Regex DisplayNameRegEx();

	[GeneratedRegex("tmi-sent-ts=(\\d+)")]
	private static partial Regex TimeStampRegEx();

	[GeneratedRegex("id=([^;]*)")]
	private static partial Regex MessageIdRegEx();

	[GeneratedRegex("PRIVMSG #(.*) :(.*)$")]
	private static partial Regex ChatMessageRegEx();

	[GeneratedRegex("WHISPER (.*) :(.*)$")]
	private static partial Regex WhisperMessageRegEx();

	//internal static readonly Regex reUserName = UserNameRegEx();
	//internal static readonly Regex reBadges = BadgesRegEx();
	//internal static readonly Regex reDisplayName = DisplayNameRegEx();
	//internal static readonly Regex reTimestamp = TimeStampRegEx();
	//internal static readonly Regex reMessageId = MessageIdRegEx();

	//internal Regex reChatMessage;
	//internal Regex reWhisperMessage;

	public event EventHandler<NewMessageEventArgs>? NewMessage;

	private DateTime? _NextReset;

	internal ChatClient(string channelName, string chatBotName, string oauthToken, ILogger logger)
	{

		ChannelName = channelName;
		ChatBotName = chatBotName;
		_OAuthToken = oauthToken;
		Logger = logger;

		//reChatMessage = new Regex($@"PRIVMSG #{channelName} :(.*)$");
		//reWhisperMessage = new Regex($@"WHISPER {chatBotName} :(.*)$");

		_Shutdown = new CancellationTokenSource();

	}

	~ChatClient()
	{

		try
		{
			Logger?.LogError("GC the ChatClient");
		}
		catch { }
		// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		Dispose(false);
	}

	[MemberNotNull(nameof(_TcpClient), nameof(_InputStream), nameof(_OutputStream), nameof(_ReceiveMessagesThread))]
	public void Init()
	{

		Connect();

		_ReceiveMessagesThread = new Thread(ReceiveMessagesOnThread);
		_ReceiveMessagesThread.Start();

	}

	public ILogger Logger { get; }

	public string ChannelName { get; }
	public string ChatBotName { get; }

	private readonly string _OAuthToken;
	private readonly CancellationTokenSource _Shutdown;

	[MemberNotNull(nameof(_TcpClient), nameof(_InputStream), nameof(_OutputStream))]
	private void Connect()
	{

		_TcpClient = new TcpClient("irc.chat.twitch.tv", 80);

		_InputStream = new StreamReader(_TcpClient.GetStream());
		_OutputStream = new StreamWriter(_TcpClient.GetStream());

		Logger.LogTrace("Beginning IRC authentication to Twitch");
		_OutputStream.WriteLine("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership");
		_OutputStream.WriteLine($"PASS oauth:{_OAuthToken}");
		_OutputStream.WriteLine($"NICK {ChatBotName}");
		_OutputStream.WriteLine($"USER {ChatBotName} 8 * :{ChatBotName}");
		_OutputStream.Flush();

		_OutputStream.WriteLine($"JOIN #{ChannelName}");
		_OutputStream.Flush();

		//Connected?.Invoke(this, new ChatConnectedEventArgs());

	}

	private void SendMessage(string message, bool flush = true)
	{

		var throttled = CheckThrottleStatus();

		Thread.Sleep(throttled.GetValueOrDefault(TimeSpan.FromSeconds(0)));

		if (_OutputStream is null)
		{
			Init();
		}

		_OutputStream.WriteLine(message);
		if (flush)
		{
			_OutputStream.Flush();
		}
	}

	private TimeSpan? CheckThrottleStatus()
	{

		var throttleDuration = TimeSpan.FromSeconds(30);
		//var maximumCommands = 100;

		if (_NextReset == null || _NextReset.Value < DateTime.UtcNow)
		{
			_NextReset = DateTime.UtcNow.Add(throttleDuration);
		}

		// TODO: Finish checking and enforcing the chat throttling

		return null;


	}

	/// <summary>
	/// Public async interface to post messages to channel
	/// </summary>
	/// <param name="message"></param>
	public void PostMessage(string message)
	{

		var fullMessage = $":{ChatBotName}!{ChatBotName}@{ChatBotName}.tmi.twitch.tv PRIVMSG #{ChannelName} :{message}";

		SendMessage(fullMessage);

	}

	public void WhisperMessage(string message, string userName)
	{

		var fullMessage = $":{ChatBotName}!{ChatBotName}@{ChatBotName}.tmi.twitch.tv PRIVMSG #jtv :/w {userName} {message}";
		SendMessage(fullMessage);

	}

	private void ReceiveMessagesOnThread()
	{

		var lastMessageReceivedTimestamp = DateTime.Now;
		var errorPeriod = TimeSpan.FromSeconds(60);

		while (true)
		{

			Thread.Sleep(50);

			if (DateTime.Now.Subtract(lastMessageReceivedTimestamp) > errorPeriod)
			{
				Logger.LogError("Haven't received a message in {errorPeriod} seconds", errorPeriod.TotalSeconds);
				lastMessageReceivedTimestamp = DateTime.Now;
			}

			if (_Shutdown.IsCancellationRequested)
			{
				break;
			}

			if (_TcpClient?.Connected ?? false && _TcpClient.Available > 0)
			{

				var msg = ReadMessage();
				if (string.IsNullOrEmpty(msg))
				{
					continue;
				}

				lastMessageReceivedTimestamp = DateTime.Now;
				Logger.LogTrace("> {msg}", msg);

				// Handle the Twitch keep-alive
				if (msg.StartsWith("PING"))
				{
					Logger.LogWarning("Received PING from Twitch... sending PONG");
					SendMessage($"PONG :{msg.Split(':')[1]}");
					continue;
				}

				ProcessMessage(msg);

			}
			else if (_TcpClient is null || !_TcpClient.Connected)
			{
				// Reconnect
				Logger.LogWarning("Disconnected from Twitch.. Reconnecting in 2 seconds");
				Thread.Sleep(2000);
				Init();
				return;
			}

		}

		Logger.LogWarning("Exiting ReceiveMessages Loop");

	}

	private void ProcessMessage(string msg)
	{

		// Logger.LogTrace("Processing message: " + msg);

		var userName = UserNameRegEx().Match(msg).Groups[1].Value;
		//if (userName.Equals(ChatBotName, StringComparison.InvariantCultureIgnoreCase)) return; // Exit and do not process if the bot posted this message


		//if (!string.IsNullOrEmpty(userName) && msg.Contains($" JOIN #{ChannelName}"))
		//{
		//	UserJoined?.Invoke(this, new ChatUserJoinedEventArgs { UserName = userName });
		//}

		// Review messages sent to the channel

		var chat = ChatMessageRegEx().Match(msg);
		if (chat.Success && chat.Groups[1].Value== ChannelName)
		{

			var displayName = DisplayNameRegEx().Match(msg).Groups[1].Value;
			var timestamp = long.Parse(TimeStampRegEx().Match(msg).Groups[1].Value);
			var messageId = MessageIdRegEx().Match(msg).Groups[1].Value;

			var badges = BadgesRegEx().Match(msg).Groups[1].Value.Split(',');

			var message = chat.Groups[2].Value;
			Logger.LogTrace("Message received from '{userName}': {message}", userName, message);
			NewMessage?.Invoke(this, new NewMessageEventArgs
			{
				MessageId = messageId,
				UserName = userName,
				DisplayName = displayName,
				Message = message,
				Badges = badges,
				Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
			});

		} else
		{
			//var whisper = WhisperMessageRegEx().Match(msg);
			//if (whisper.Success && whisper.Groups[1].Value== ChatBotName)
			//{
			//	var message = whisper.Groups[2].Value;
			//	Logger.LogTrace("Whisper received from '{userName}': {message}", userName, message);

			//	NewMessage?.Invoke(this, new NewMessageEventArgs
			//	{
			//		UserName = userName,
			//		Message = message,
			//		Badges = Array.Empty<string>(),
			//		IsWhisper = true
			//	});
			//}
		}
		
	}

	private string ReadMessage()
	{

		string? message = null;

		if (_InputStream is null)
		{
			Init();
		}

		try
		{
			message = _InputStream.ReadLine();
		}
		catch (Exception ex)
		{
			Logger.LogError(ex, "Error reading messages: {message}", ex.Message);
		}

		return message ?? "";

	}

	#region IDisposable Support
	private bool _DisposedValue = false; // To detect redundant calls
	private Thread? _ReceiveMessagesThread;

	protected virtual void Dispose(bool disposing)
	{

		try
		{
			Logger?.LogWarning("Disposing of ChatClient");
		}
		catch { }

		if (!_DisposedValue)
		{
			if (disposing)
			{
				_Shutdown.Cancel();
			}

			_TcpClient?.Dispose();
			_DisposedValue = true;
		}
	}

	// This code added to correctly implement the disposable pattern.
	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	#endregion
}

public static class BufferHelpers
{

	public static ArraySegment<byte> ToBuffer(this string text)
	{

		return Encoding.UTF8.GetBytes(text);

	}

}
