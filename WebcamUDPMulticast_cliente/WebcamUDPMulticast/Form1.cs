using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// UDP y Multicast
using System.Net;
using System.Net.Sockets;
using System.IO;

// CHAT
using System.Threading;

// IMAGE
using System.Drawing.Imaging;

// AUDIO
using Microsoft.DirectX.DirectSound;
using ALaw;
using Timer = System.Windows.Forms.Timer;

// RTP
//using RTPStream;
namespace WebcamUDPMulticast
{
    public partial class Form1 : Form
    {
        // Multicast
        private IPAddress multicast = IPAddress.Parse("224.0.0.4");
        private static UdpClient videoclient, chat1client, chat2client, audioclient;
        private static IPEndPoint videoremote, chat1remote, chat2remote, audioremote;
        private int videoport = 45040;
        private int chat1port = 45041;
        private int chat2port = 45042;
        private int audioport = 45043;

        // Audio
        //private Guid record_source;
        private short channels = 1;
        private short bitsPerSample = 16;
        private int samplesPerSecond = 22050;

        private Thread receivedAudio;
        private Device device;
        private WaveFormat waveFormat;
        private BufferDescription bufferDesc;
        private SecondaryBuffer bufferplayback;

        // Video
        private Thread receivedVideo;
        private byte[] received;
        private Image frame;

        // Chat
        private String username;
        private String msg;
        private byte[] encodedmsg;

        // QoA
        private Timer timer1;
        private Timer timer2;
        private int num_video, num_audio, total_audio, total_video;
        private long video_prev_time, audio_prev_time;
        private int video_prev_timestamp, audio_prev_timestamp;
        private double audio_delay, audio_jitter;
        private double video_delay, video_jitter;
        private float lost_audio, lost_video;
        private List<int> sequence_audio, sequence_video;


        public Form1()
        {
            InitializeComponent();

            label11.Text = "";
            richTextBox1.Enabled = false;
            button1.Enabled = false;
            button2.Enabled = false;
            listBox1.Items.Add("*** Enter your username and press login ***");
        }
        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void Form1_Load(object sender, EventArgs e)
        {
            Task t1 = new Task(VisualizeImage);
            t1.Start();

        }

        private void VisualizeImage()
        {
            try
            {
                using (UdpClient udpClient = new UdpClient())
                {
                    IPAddress multicastAddress = IPAddress.Parse("224.0.0.4");
                    udpClient.JoinMulticastGroup(multicastAddress);

                    IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 8080);

                    udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udpClient.Client.Bind(remoteEndPoint);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing image visualization: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        public Image byteArrayToImage(byte[] byteArrayIn)
        {
            using (var ms = new MemoryStream(byteArrayIn))
            {
                return Image.FromStream(ms);
            }
        }


        private void button1_Click(object sender, EventArgs e)
        {
            // Send message to chat
            if (!string.IsNullOrWhiteSpace(richTextBox1.Text))
            {
                // Send to multicast
                try
                {
                    string message = $"{username}: {richTextBox1.Text}";
                    byte[] encodedMessage = Encoding.UTF8.GetBytes(message);
                    chat2client.Send(encodedMessage, encodedMessage.Length, chat2remote);

                    // Adding a message to a list box
                    listBox1.Items.Add(message);
                    richTextBox1.Clear();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error sending the chat message: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            DialogResult diag = MessageBox.Show("Do you want to leave the process?");
            if (diag == DialogResult.OK)
            {
                Application.ExitThread();
                Application.Exit();
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            // Click the Connect button

            // Save Chat User
            if (string.IsNullOrWhiteSpace(richTextBox2.Text))
            {
                richTextBox2.Text = "AnonymousClient";
                username = "AnonymousClient";
            }
            else
            {
                username = richTextBox2.Text;
            }
            richTextBox2.Enabled = false;

            // Update button and control status
            button2.Enabled = true;
            button3.Enabled = false;
            richTextBox1.Enabled = true;
            button1.Enabled = true;
            listBox1.Items.Clear();
            listBox1.Items.Add($"*** Welcome {username} to the chat room ***");

            // Initialising the communication channel
            try
            {
                InitializeVideoReception();
                InitializeAudioReception();
                InitializeChatReception();
                InitializeChatSending();

                // Start chat thread
                Thread chatThread = new Thread(ChatThread);
                chatThread.Start();
                CheckForIllegalCrossThreadCalls = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing communication channels: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                checkBox4.Checked = true;
            }
        }

        private void InitializeVideoReception()
        {
            // Video reception
            videoclient = new UdpClient();
            videoremote = new IPEndPoint(IPAddress.Any, videoport);
            videoclient.Client.Bind(videoremote);
            videoclient.JoinMulticastGroup(multicast);
            lost_video = 0;
            sequence_video = new List<int>();

            receivedVideo = new Thread(VideoThread);
            receivedVideo.Start();

            // video timer
            timer1 = new Timer
            {
                Interval = 1000 // Every 1000 ms
            };
            timer1.Tick += analyzeVideo;
            timer1.Start();

            // Video QoA initialisation
            video_prev_time = 0;
            video_prev_timestamp = 0;
        }

        private void InitializeAudioReception()
        {
            // audio reception
            audioclient = new UdpClient();
            audioremote = new IPEndPoint(IPAddress.Any, audioport);
            audioclient.Client.Bind(audioremote);
            audioclient.JoinMulticastGroup(multicast);

            // Initialising the audio
            InitCaptureSound();
            lost_audio = 0;
            sequence_audio = new List<int>();

            receivedAudio = new Thread(AudioThread);
            receivedAudio.Start();

            // timer
            timer2 = new Timer
            {
                Interval = 1000 // Every 1000 ms
            };
            timer2.Tick += analyzeAudio;
            timer2.Start();

            // Audio QoA initialisation
            audio_prev_time = 0;
            audio_prev_timestamp = 0;
        }

        private void InitializeChatReception()
        {
            // Receive Chat
            chat1client = new UdpClient(chat1port);
            chat1client.JoinMulticastGroup(multicast);
        }

        private void InitializeChatSending()
        {
            // Send Chat
            chat2client = new UdpClient();
            chat2remote = new IPEndPoint(multicast, chat2port);
            chat2client.JoinMulticastGroup(multicast);
        }

        private void ChatThread()
        {
            // We keep the socket on standby
            while (true)
            {
                byte[] received = chat1client.Receive(ref chat1remote);

                msg = Encoding.UTF8.GetString(received);
                listBox1.Items.Add(msg);
            }
        }


        private void VideoThread()
        {
            while (true)
            {
                try
                {
                    byte[] receivedData = videoclient.Receive(ref videoremote);

                    // Convert to image
                    byte[] imageBytes = GetRtpPayload(receivedData);
                    num_video++;

                    // Save Serial Number
                    int sequenceNumber = GetRtpSequenceNumber(receivedData);
                    sequence_video.Add(sequenceNumber);

                    // Get timestamp
                    long currentTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                    int timestamp = GetRtpTimestamp(receivedData);

                    // Computational latency and jitter
                    if (video_prev_timestamp == 0)
                    {
                        video_jitter = 0;
                    }
                    else
                    {
                        video_delay = (currentTime - video_prev_time) - (timestamp * 0.00001 - video_prev_timestamp * 0.00001);
                        video_jitter = video_jitter + (Math.Abs(video_delay) - video_jitter) / 16;
                    }

                    // Storing information about timestamps and times
                    video_prev_timestamp = timestamp;
                    video_prev_time = currentTime;

                    // Update Tags
                    UpdateLabel(label7, $"Delay: {video_delay:0.00} ms");
                    UpdateLabel(label8, $"Jitter: {video_jitter:0.00} ms");

                    Image frameImage = Image.FromStream(new MemoryStream(imageBytes));
                    UpdatePictureBox(pictureBox1, frameImage);

                    // Marked as receiving RTP video
                    UpdateCheckBox(checkBox2, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error receiving video: {ex.Message}");
                }
            }
        }

        private byte[] GetRtpPayload(byte[] rtpPacket)
        {
            // Assuming an RTP header length of 12 bytes
            const int headerLength = 12;
            byte[] payload = new byte[rtpPacket.Length - headerLength];
            Array.Copy(rtpPacket, headerLength, payload, 0, payload.Length);
            return payload;
        }

        private int GetRtpSequenceNumber(byte[] rtpPacket)
        {
            // Sequence number in RTP header at 2-3 bytes
            return (rtpPacket[2] << 8) | rtpPacket[3];
        }

        private int GetRtpTimestamp(byte[] rtpPacket)
        {
            // The timestamp in the RTP header is located at 4-7 bytes
            return (rtpPacket[4] << 24) | (rtpPacket[5] << 16) | (rtpPacket[6] << 8) | rtpPacket[7];
        }

        private void UpdateLabel(Label label, string text)
        {
            if (label.InvokeRequired)
            {
                label.Invoke(new Action(() => label.Text = text));
            }
            else
            {
                label.Text = text;
            }
        }

        private void UpdatePictureBox(PictureBox pictureBox, Image image)
        {
            if (pictureBox.InvokeRequired)
            {
                pictureBox.Invoke(new Action(() => pictureBox.Image = new Bitmap(image, new Size(pictureBox.Width, pictureBox.Height))));
            }
            else
            {
                pictureBox.Image = new Bitmap(image, new Size(pictureBox.Width, pictureBox.Height));
            }
        }

        private void UpdateCheckBox(CheckBox checkBox, bool isChecked)
        {
            if (checkBox.InvokeRequired)
            {
                checkBox.Invoke(new Action(() => checkBox.Checked = isChecked));
            }
            else
            {
                checkBox.Checked = isChecked;
            }
        }


        public void InitCaptureSound()
        {
            device = new Device();
            device.SetCooperativeLevel(this, CooperativeLevel.Normal);

            // We create the WaveFormat
            waveFormat = new WaveFormat
            {
                BitsPerSample = bitsPerSample,                                                                  // 16 bits
                BlockAlign = (short)(channels * (bitsPerSample / (short)8)),
                Channels = channels,                                                                            // Stereo
                AverageBytesPerSecond = (short)(channels * (bitsPerSample / (short)8)) * samplesPerSecond,      // 22kHz
                SamplesPerSecond = samplesPerSecond,                                                            // 22kHz
                FormatTag = WaveFormatTag.Pcm
            };

            bufferDesc = new BufferDescription
            {
                BufferBytes = waveFormat.AverageBytesPerSecond / 5,
                Format = waveFormat
            };

            bufferplayback = new SecondaryBuffer(bufferDesc, device);
        }


        private void AudioThread()
        {
            try
            {
                byte[] receivedData;

                while (!button3.Enabled)
                {
                    try
                    {
                        receivedData = audioclient.Receive(ref audioremote);

                        byte[] audioBytes = GetRtpPayload(receivedData);
                        num_audio++;

                        int sequenceNumber = GetRtpSequenceNumber(receivedData);
                        sequence_audio.Add(sequenceNumber);
                   
                        long currentTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                        int timestamp = GetRtpTimestamp(receivedData);
  
                        if (audio_prev_timestamp == 0)
                        {
                            audio_jitter = 0;
                        }
                        else
                        {
                            audio_delay = (currentTime - audio_prev_time) - (timestamp * 0.000125 - audio_prev_timestamp * 0.000125);
                            audio_jitter = audio_jitter + (Math.Abs(audio_delay) - audio_jitter) / 16;
                        }
                        audio_prev_timestamp = timestamp;
                        audio_prev_time = currentTime;

                        // Update Tags
                        UpdateLabel(label5, $"Delay: {audio_delay:0.00} ms");
                        UpdateLabel(label6, $"Jitter: {audio_jitter:0.00} ms");

                        // Marked as receiving RTP audio
                        UpdateCheckBox(checkBox1, true);

                        // Decode audio data
                        byte[] decodedAudioData = new byte[audioBytes.Length * 2];
                        ALawDecoder.ALawDecode(audioBytes, out decodedAudioData);

                        // Playing audio data
                        UpdateCheckBox(checkBox3, true);
                        bufferplayback = new SecondaryBuffer(bufferDesc, device);
                        bufferplayback.Write(0, decodedAudioData, LockFlag.None);
                        bufferplayback.Play(0, BufferPlayFlags.Default);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error receiving audio: {ex.Message}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in audio reception thread: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private byte[] GetRtpPayload2(byte[] rtpPacket)
        {
            const int headerLength = 12;
            byte[] payload = new byte[rtpPacket.Length - headerLength];
            Array.Copy(rtpPacket, headerLength, payload, 0, payload.Length);
            return payload;
        }

        private int GetRtpSequenceNumber2(byte[] rtpPacket)
        {
            return (rtpPacket[2] << 8) | rtpPacket[3];
        }

        private int GetRtpTimestamp2(byte[] rtpPacket)
        {
            return (rtpPacket[4] << 24) | (rtpPacket[5] << 16) | (rtpPacket[6] << 8) | rtpPacket[7];
        }

        private void UpdateLabel2(Label label, string text)
        {
            if (label.InvokeRequired)
            {
                label.Invoke(new Action(() => label.Text = text));
            }
            else
            {
                label.Text = text;
            }
        }

        private void UpdateCheckBox2(CheckBox checkBox, bool isChecked)
        {
            if (checkBox.InvokeRequired)
            {
                checkBox.Invoke(new Action(() => checkBox.Checked = isChecked));
            }
            else
            {
                checkBox.Checked = isChecked;
            }
        }


        #region RTP
        private uint getTimestampRTP(byte[] receivedData)
        {
            uint timestamp;

            MemoryStream packet = new MemoryStream(receivedData);
            packet.Seek(4, SeekOrigin.Begin);
            timestamp = ReadUInt32(packet);

            return timestamp;
        }

        private ushort getSequenceRTP(byte[] receivedData)
        {
            ushort seq;

            MemoryStream packet = new MemoryStream(receivedData);
            packet.Seek(2, SeekOrigin.Begin);
            seq = (ushort)ReadUInt16(packet);

            return seq;

        }

        private byte[] getPayloadRTP(byte[] receivedData)
        {
            // Extract the payload from the RTP package
            MemoryStream packet = new MemoryStream(receivedData);
            byte[] payload;
            byte[] buffer = new byte[receivedData.Length - 12];     // Remove the size of the RTP header
            using (MemoryStream ms = new MemoryStream())            // Save the payload in a new memory stream
            {
                int read;
                packet.Seek(12, SeekOrigin.Begin);                  // We set the pointer from the RTP header
                while ((read = packet.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }
                payload = ms.ToArray();
            }

            return payload;
        }
        #endregion

        #region utils
        private static uint ReadUInt32(Stream stream)
        {
            return (uint)((stream.ReadByte() << 24)
                            + (stream.ReadByte() << 16)
                            + (stream.ReadByte() << 8)
                            + (stream.ReadByte()));
        }

        private static ushort ReadUInt16(Stream stream)
        {
            return (ushort)((stream.ReadByte() << 8)
                            + (stream.ReadByte()));
        }
        #endregion

        private void analyzeVideo(object sender, EventArgs e)
        {
            total_video += num_video;

            // Calculate Paq/s
            label4.Text = String.Format("{0} paq/s", num_video);
            num_video = 0;

            // Check the sequence numbers
            try
            {
                List<int> copy_video = sequence_video;
                copy_video.Sort();
                List<int> result_video = Enumerable.Range(copy_video.First(), copy_video.Count()).Except(sequence_video).ToList();
                lost_video += result_video.Count();
            }
            catch (Exception)
            {

            }

            sequence_video.Clear();

            // Calculate Paqs lost %
            float video_loss = (lost_video / total_video) * 100;
            label10.Text = String.Format("Loss {0} %", video_loss);
        }

        private void analyzeAudio(object sender, EventArgs e)
        {
            total_audio += num_audio;

            // Calculate Paq/s
            label3.Text = String.Format("{0} paq/s", num_audio);
            num_audio = 0;

            // Check the sequence numbers
            try
            {
                List<int> copy_audio = sequence_audio;
                copy_audio.Sort();
                List<int> result_audio = Enumerable.Range(copy_audio.First(), copy_audio.Count()).Except(sequence_audio).ToList();
                lost_audio += result_audio.Count();
            }
            catch (Exception)
            {

            }

            sequence_audio.Clear();

            // Calculate Paqs lost %
            float audio_loss = (lost_audio / total_audio) * 100;
            label9.Text = String.Format("Perdidas {0} %", audio_loss);


        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {

        }


    }
}
