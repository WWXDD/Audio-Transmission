using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Touchless.Vision.Camera;

// UDP y Multicast.
using System.Net.Sockets;
using System.Net;
using System.IO;

// IMAGE
using System.Drawing.Imaging;
using System.Threading;

// AUDIO
using Microsoft.DirectX.DirectSound;
using ALaw;

// RTP
using RTPStream;

using Timer = System.Windows.Forms.Timer;
using System.Security.Cryptography;

namespace WebcamUDPMulticast
{
    public partial class Form1 : Form
    {

        // Camera Config
        private CameraFrameSource _frameSource;
        private static Bitmap _latestFrame;
        private MemoryStream jpegFrame;
        //Byte img = 1;



        // Audio Config
        private Guid record_source;
        private readonly short channels = 1;
        private readonly short bitsPerSample = 16;
        private readonly int samplesPerSecond = 22050;

        private AutoResetEvent autoResetEvent;
        private Notify notify;

        private Thread audiosender;

        private Device device;
        private Capture capture;
        private WaveFormat waveFormat;
        private BufferDescription bufferDesc;
        private SecondaryBuffer bufferplayback;
        private int buffersize = 100000;
        private CaptureBuffer captureBuffer;
        private CaptureBufferDescription captureBuffDesc;

        // Multicast
        private readonly int videoport = 45040;
        private readonly int chat1port = 45041;
        private readonly int chat2port = 45042;
        private readonly int audioport = 45043;
        private IPAddress multicast = IPAddress.Parse("224.0.0.4");
        private static UdpClient videoserver, chat1server, chat2server, audioserver;
        private static IPEndPoint videoremote, chat1remote, chat2remote, audioremote;

        private RTP video;
        private RTP audio;

        // Chat
        private String username;
        private String msg;
        private byte[] encodedmsg;

        // QoS
        private Timer timer1;
        private Timer timer2;
        private int num_video;
        private int num_audio;

        public Form1()
        {
            InitializeComponent();
            richTextBox1.Enabled = false;
            button1.Enabled = false;
            listBox1.Items.Add("*** Enter your username and click connect ***");
            button3.Enabled = false;
            button6.Enabled = false;
            button5.Enabled = false;
            button8.Enabled = false;
            button6.Visible = false;
        }


        private void Form1_Load(object sender, EventArgs e)

        {
            InitCamera();

            InitAudioCombo();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            // Send message to chat
            if (!string.IsNullOrWhiteSpace(richTextBox1.Text))
            {
                // Send to multicast
                try
                {
                    string msg = string.Format("{0}: {1}", username, richTextBox1.Text);
                    byte[] encodedmsg = Encoding.UTF8.GetBytes(msg);
                    chat1server.Send(encodedmsg, encodedmsg.Length, chat1remote);

                    // Adding a message to a list box
                    listBox1.Items.Add(msg);
                    richTextBox1.Clear(); // Clear the contents of the text box
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error sending the chat message: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            // We have clicked the connect button

            // save the chat user
            if (richTextBox2.Text == "")
            {
                richTextBox2.Text = "Anonymous";
                richTextBox2.Enabled = false;
                username = "Anonymous";
            }
            else
            {
                username = richTextBox2.Text;
                richTextBox2.Enabled = false;
            }

            button2.Enabled = false;
            button3.Enabled = true;
            richTextBox1.Enabled = true;
            button1.Enabled = true;
            listBox1.Items.Clear();
            listBox1.Items.Add(String.Format("***  Welcome {0} to the chat room ***", username));

            // Initialising communication channels
            try
            {
                // Video transmission
                videoserver = new UdpClient();
                videoremote = new IPEndPoint(multicast, videoport);
                // Implementing my RTP class
                video = new RTP("video1", videoserver, multicast, videoremote);

                // Timer for paq/s
                timer1 = new Timer();
                timer1.Tick += new EventHandler(analyzeVideo);  //Take parameters from the TX
                timer1.Interval = 1000;                         // Every 1000 ms
                timer1.Start();

                // Audio transmission
                audioserver = new UdpClient();
                audioremote = new IPEndPoint(multicast, audioport);
                // Implementation my RTP class
                audio = new RTP("audio1", audioserver, multicast, audioremote);

                // We start the audio
                InitAudioCapture();
                audiosender = new Thread(new ThreadStart(SendAudio));

                // Timer for paq/s
                timer2 = new Timer();
                timer2.Tick += new EventHandler(analyzeAudio);  // Take parameters from the TX
                timer2.Interval = 1000;                         //  Every 1000 ms
                timer2.Start();

                // Send chat
                chat1server = new UdpClient();
                chat1remote = new IPEndPoint(multicast, chat1port);
                chat1server.JoinMulticastGroup(multicast);

                // Receive chat
                chat2server = new UdpClient(chat2port);
                chat2remote = null;
                chat2server.JoinMulticastGroup(multicast);

                checkBox4.Checked = true;

                Thread t = new Thread(this.ChatThread);
                t.Start();
                CheckForIllegalCrossThreadCalls = false;
            }
            catch (Exception excp)
            {
                MessageBox.Show(excp.ToString());
            }
            finally
            {
                checkBox1.Checked = true;
            }
        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {

        }

        public void OnImageCaptured(Touchless.Vision.Contracts.IFrameSource frameSource, Touchless.Vision.Contracts.Frame frame, double fps)
        {
            _latestFrame = frame.Image;
            pictureBox1.Invalidate();
        }


        private void button4_Click(object sender, EventArgs e)
        {
            // Transmit Video
            if (button2.Enabled == false)
            {
                comboBoxCameras.Enabled = false;
                button4.Enabled = false;
                button5.Enabled = true;
                checkBox2.Checked = true;
            }
        }


        private void button5_Click(object sender, EventArgs e)
        {
            // Stop Transmit Video
            if (button2.Enabled == false)
            {
                comboBoxCameras.Enabled = true;
                button4.Enabled = true;
                button5.Enabled = false;
                checkBox2.Checked = false;
            }
        }


        private void button7_Click(object sender, EventArgs e)
        {
            // Start Transmit Audio
            if (button2.Enabled == false)
            {
                comboBoxAudio.Enabled = false;
                button7.Enabled = false;
                button8.Enabled = true;
                checkBox3.Checked = true;

                // Start Audio Send
                audiosender.Start();

            }
        }

        private void button8_Click(object sender, EventArgs e)
        {
            // Stop Transmit Audio
            if (button2.Enabled == false)
            {
                comboBoxAudio.Enabled = true;
                button7.Enabled = true;
                button8.Enabled = false;
                checkBox3.Checked = false;
            }
        }


       private void button3_Click(object sender, EventArgs e)
        {
            DialogResult diag = MessageBox.Show("Exiting the application?", "Exit Confirmation", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (diag == DialogResult.OK)
                    {
                       // Close all threads and resources
                       Application.ExitThread();
                       Application.Exit();
                     }
        }





        private void setFrameSource(CameraFrameSource cameraFrameSource)
        {
            if (_frameSource == cameraFrameSource)
                return;

            _frameSource = cameraFrameSource;
        }

     
        private void drawLatestImage(object sender, PaintEventArgs e)
        {
            if (_latestFrame != null)
            {
                _latestFrame = new Bitmap(_latestFrame, new Size(320, 240));
                e.Graphics.DrawImage(_latestFrame, 0, 0, _latestFrame.Width, _latestFrame.Height);

                UdpClient udpServer = new UdpClient();
                IPAddress multicastaddress = IPAddress.Parse("224.0.0.4");
                udpServer.JoinMulticastGroup(multicastaddress);

                IPEndPoint remote = new IPEndPoint(multicastaddress, 8080);

                Byte[] buffer = ImageToByteArray(_latestFrame);
                udpServer.Send(buffer, buffer.Length, remote);

                if (button4.Enabled == false && button2.Enabled == false)
                {
                    // transmit the image
                    // compress the frame
                    Bitmap cFrame = new Bitmap(_latestFrame, new Size(320, 240));

                    // Conversion to JPEG
                    jpegFrame = new MemoryStream();
                    cFrame.Save(jpegFrame, ImageFormat.Jpeg);

                    // send through the channel
                    video.sendJPEG(jpegFrame);
                    num_video++;
                }

            }
            
        }


        private void ChatThread()
        {
            // keep the socket on standby
            while (true)
            {
                byte[] received = chat2server.Receive(ref chat2remote);

                msg = Encoding.UTF8.GetString(received, 0, received.Length);
                listBox1.Items.Add(msg);
            }
        }


        private void InitCamera()
        {
            try
            {
                // Check for available cameras
                if (CameraService.AvailableCameras.Count() == 0)
                {
                    throw new Exception("No available cameras found.");
                }

                // Clear and fill the camera selection box
                comboBoxCameras.Items.Clear();
                foreach (Camera cam in CameraService.AvailableCameras)
                {
                    comboBoxCameras.Items.Add(cam);
                }

                // Check that the camera is added and set the default selection
                if (comboBoxCameras.Items.Count > 0)
                {
                    comboBoxCameras.SelectedIndex = 0;
                    comboBoxCameras.Select();
                }
                else
                {
                    throw new Exception("No cameras added to comboBox.");
                }

                // Get the selected camera and configure it
                Camera selectedCamera = (Camera)comboBoxCameras.SelectedItem;
                if (selectedCamera != null)
                {
                    _frameSource = new CameraFrameSource(selectedCamera);
                    _frameSource.Camera.CaptureWidth = 320;
                    _frameSource.Camera.CaptureHeight = 240;
                    _frameSource.Camera.Fps = 20;
                    _frameSource.NewFrame += OnImageCaptured;
                    pictureBox1.Paint += new PaintEventHandler(drawLatestImage);

                    // Start frame capture
                    _frameSource.StartFrameCapture();
                }
                else
                {
                    throw new Exception("Selected camera is null.");
                }
            }
            catch (Exception ex)
            {
                // Using detailed error messages
                Console.WriteLine($"Error initializing camera: {ex.Message}\n{ex.StackTrace}");
            }
        }



        public byte[] ImageToByteArray(System.Drawing.Image imageIn)
        {
            using (var ms = new MemoryStream())
            {
                imageIn.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                return ms.ToArray();
            }
        }


        private void comboBoxAudio_SelectedIndexChanged(object sender, EventArgs e)
        {
            record_source = (Guid)comboBoxAudio.SelectedValue;
        }

        class capture_device
        {
            public Guid DriverGuid { get; set; }
            public string Description { get; set; }
            public string ModuleName { get; set; }
        }

        List<capture_device> devices = new List<capture_device>();

        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void InitAudioCombo()
        {
            try
            {
                // Creating a Device List
                CaptureDevicesCollection captureDevicesCollection = new CaptureDevicesCollection();
                comboBoxAudio.ValueMember = "DriverGuid";
                comboBoxAudio.DisplayMember = "Description";

                // Iterate through the devices and add to the list
                List<capture_device> devices = new List<capture_device>();
                foreach (DeviceInformation item in captureDevicesCollection)
                {
                    devices.Add(new capture_device()
                    {
                        Description = item.Description,
                        ModuleName = item.ModuleName,
                        DriverGuid = item.DriverGuid
                    });
                }

                // Bind device list to comboBoxAudio
                comboBoxAudio.DataSource = devices;

                // Make sure the device list is not empty
                if (devices.Count > 0)
                {
                    comboBoxAudio.SelectedIndex = 0; // Setting the default selection of the first device
                    record_source = (Guid)comboBoxAudio.SelectedValue;
                }
                else
                {
                    throw new Exception("No audio devices found.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing audio combo box: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }




        private void InitAudioCapture()
        {
            // Set up DirectSound
            device = new Device();
            device.SetCooperativeLevel(this, CooperativeLevel.Normal);

            capture = new Capture(record_source);

            // Create Waveformat
            waveFormat = new WaveFormat
            {
                BitsPerSample = bitsPerSample,                                                                  // 16 bits
                BlockAlign = (short)(channels * (bitsPerSample / (short)8)),
                Channels = channels,                                                                            // Stereo
                AverageBytesPerSecond = (short)(channels * (bitsPerSample / (short)8)) * samplesPerSecond,      // 22kHz
                SamplesPerSecond = samplesPerSecond,                                                            // 22kHz
                FormatTag = WaveFormatTag.Pcm
            };
            // Create CaptureBufferDescription
            captureBuffDesc = new CaptureBufferDescription
            {
                BufferBytes = waveFormat.AverageBytesPerSecond / 5,
                Format = waveFormat
            };

            bufferDesc = new BufferDescription
            {
                BufferBytes = waveFormat.AverageBytesPerSecond / 5,
                Format = waveFormat
            };

            bufferplayback = new SecondaryBuffer(bufferDesc, device);
            buffersize = captureBuffDesc.BufferBytes;
            }

        private void CreateNotifyPositions()
        {
            try
            {
                autoResetEvent = new AutoResetEvent(false);
                notify = new Notify(captureBuffer);
                BufferPositionNotify bufferPositionNotify1 = new BufferPositionNotify
                {
                    Offset = buffersize / 2 - 1,
                    EventNotifyHandle = autoResetEvent.SafeWaitHandle.DangerousGetHandle()
                };
                BufferPositionNotify bufferPositionNotify2 = new BufferPositionNotify
                {
                    Offset = buffersize - 1,
                    EventNotifyHandle = autoResetEvent.SafeWaitHandle.DangerousGetHandle()
                };

                notify.SetNotificationPositions(new BufferPositionNotify[] { bufferPositionNotify1, bufferPositionNotify2 });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error on CreatePositionNotify", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SendAudio()
        {
            try
            {
                // We capture the audio and send it over the network.
                int halfbuffer = buffersize / 2;
                captureBuffer = new CaptureBuffer(captureBuffDesc, capture);
                CreateNotifyPositions();
                captureBuffer.Start(true);
                bool readFirstBufferPart = true;
                int offset = 0;
                MemoryStream memStream = new MemoryStream(halfbuffer);

                while (!button7.Enabled)
                {
                    // We look forward to an event
                    autoResetEvent.WaitOne();
                    // We put the pointer at the beginning of the MS
                    memStream.Seek(0, SeekOrigin.Begin);
                    // We read the Capture Buffer and save it in the first half.
                    captureBuffer.Read(offset, memStream, halfbuffer, LockFlag.None);
                    readFirstBufferPart = !readFirstBufferPart;
                    offset = readFirstBufferPart ? 0 : halfbuffer;

                    // Prepare the data stream
                    //byte[] data = memStream.GetBuffer(); // No compression
                    byte[] data = ALawEncoder.ALawEncode(memStream.GetBuffer());

                    // We send via RTP to the user.
                    audio.sendALaw(data);
                    num_audio++;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending audio: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void analyzeVideo(object sender, EventArgs e)
        {
            // calculate the paq/s sent
            label5.Text = String.Format("{0} paq/s", num_video);
            num_video = 0;
        }

      

        private void analyzeAudio(object sender, EventArgs e)
        {
            // calculate the paq/s sent
            label6.Text = String.Format("{0} paq/s", num_audio);
            num_audio = 0;
        }


      

        private void pictureBox2_Click(object sender, EventArgs e)
        {

        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void label2_Click(object sender, EventArgs e)
        {

        }

        private void richTextBox2_TextChanged(object sender, EventArgs e)
        {

        }

        private void label3_Click(object sender, EventArgs e)
        {

        }
    }
}
