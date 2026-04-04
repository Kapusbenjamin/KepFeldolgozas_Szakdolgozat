using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.Globalization;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using OpenCvSharp;

public class ConnectionInfo
{
    public string IpAddress { get; set; }
    public int Port { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }

    public ConnectionInfo(string ipAddress, int port, string username, string password)
    {
        IpAddress = ipAddress;
        Port = port;
        Username = username;
        Password = password;
    }
}

public static class HikvisionCameraHelper
{
    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_Init();

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_Cleanup();

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_Logout(int userID);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_PTZControl_V30(int userId, uint channel, uint command, uint stop);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_PTZControl(int userID, int channel, int dwPTZCommand, int dwStop);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_CaptureJPEGPicture(int userID, int channel, string filePath);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_Logout_V30(int userId);

    public static float oneXZoom = 60 / 4;

    private static CHCNetSDK.NET_DVR_PTZPOS ptzPosStruct = new();
    public static ConnectionInfo connectionInfo = null;
    private static int userID = -1;
    private static CHCNetSDK.NET_DVR_DEVICEINFO_V30 deviceInfo;
    private static Timer _timer;
    private static readonly object _lock = new();
    private static void ResetTimer()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = new Timer(CallAfterInactivity, null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
        }
    }

    private static void CallAfterInactivity(object state)
    {
        Cleanup();
    }

    public static void StartMonitoring()
    {
        ResetTimer();
    }

    public static string LoginToCamera(string ipAddress, int port, string username, string password)
    {
        LogoutFromCamera();
        bool InitSdkSuccesful = CHCNetSDK.NET_DVR_Init();

        if (InitSdkSuccesful)
        {
            deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();

            userID = CHCNetSDK.NET_DVR_Login_V30(ipAddress, port, username, password, ref deviceInfo);

            if (userID > -1)
            {
                connectionInfo = new ConnectionInfo(ipAddress, port, username, password);
                StartMonitoring();
                return "success";
            }
        }
        return GetErrorMessage();
    }

    public static bool ReconnectToCamera()
    {
        if (connectionInfo is not null)
        {
            userID = CHCNetSDK.NET_DVR_Login_V30(connectionInfo.IpAddress, connectionInfo.Port, connectionInfo.Username, connectionInfo.Password, ref deviceInfo);
            return userID > -1;
        }
        return false;
    }

    public static bool LogoutFromCamera()
    {
        if (userID >= 0)
        {
            bool result = CHCNetSDK.NET_DVR_Logout(userID);
            StreamHelper.StopStream();
            userID = -1;
            return result;
        }
        return false;
    }

    public static async Task<string> SetPtzByAngleAndZoom(PTZCameraControlItem move, bool capture = false)
    {
        int flag = 0;
        if (userID == -1)
        {
            var reconnected = ReconnectToCamera();
            if (!reconnected)
                return "User is not logged In";
        }

        ResetTimer();

        ptzPosStruct.wAction = 1;
        ptzPosStruct.wTiltPos = ConvertAngleToCameraScale((float)move.CameraTiltPosition);
        ptzPosStruct.wPanPos = ConvertAngleToCameraScale((float)move.CameraPanPosition);
        ptzPosStruct.wZoomPos = (ushort)(float.Parse(move.ZoomRatio, CultureInfo.InvariantCulture.NumberFormat) * oneXZoom);

        try
        {
            while (flag == 0)
            {
                int nSize = Marshal.SizeOf(ptzPosStruct);
                IntPtr ptrPtzCfg = Marshal.AllocHGlobal(nSize);
                Marshal.StructureToPtr(ptzPosStruct, ptrPtzCfg, false);

                if (CHCNetSDK.NET_DVR_SetDVRConfig(userID, CHCNetSDK.NET_DVR_SET_PTZPOS, 1, ptrPtzCfg, (uint)nSize))
                {
                    Marshal.FreeHGlobal(ptrPtzCfg);
                    flag = 1;
                }
                else
                {
                    Marshal.FreeHGlobal(ptrPtzCfg);
                    return GetErrorMessage();
                }
            }

            // wait while camera moving
            bool stopped = await WaitUntilPtzStoppedAsync(userID, 1, timeoutMs: 8000);
            if (!stopped)
                return "PTZ move timeout or still moving.";

            return "success";
        }
        catch (Exception ex)
        {
            return $"Error occurred {ex.Message}";
        }
    }

    private static async Task<bool> WaitUntilPtzStoppedAsync(int userId, int channel, int timeoutMs = 5000)
    {
        var lastPos = GetPTZPos(userId, channel);
        int stableCount = 0;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(300);
            var current = GetPTZPos(userId, channel);

            if (PositionsEqual(lastPos, current))
            {
                stableCount++;
                if (stableCount >= 3) // 3 egymás utáni mérés nem változik → megállt
                    return true;
            }
            else
            {
                stableCount = 0;
            }

            lastPos = current;
        }

        return false;
    }

    private static CHCNetSDK.NET_DVR_PTZPOS GetPTZPos(int userId, int channel)
    {
        CHCNetSDK.NET_DVR_PTZPOS pos = new CHCNetSDK.NET_DVR_PTZPOS();
        uint returned = 0;
        IntPtr posPtr = Marshal.AllocHGlobal(Marshal.SizeOf(pos));

        try
        {
            Marshal.StructureToPtr(pos, posPtr, false);

            bool ok = CHCNetSDK.NET_DVR_GetDVRConfig(
                userId,
                CHCNetSDK.NET_DVR_GET_PTZPOS,
                channel,
                posPtr,
                (uint)Marshal.SizeOf(pos),
                ref returned
            );

            if (!ok)
            {
                Console.WriteLine("GetPTZPos error: " + CHCNetSDK.NET_DVR_GetLastError());
            }
            else
            {
                pos = Marshal.PtrToStructure<CHCNetSDK.NET_DVR_PTZPOS>(posPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(posPtr);
        }

        return pos;
    }

    private static bool PositionsEqual(CHCNetSDK.NET_DVR_PTZPOS a, CHCNetSDK.NET_DVR_PTZPOS b)
    {
        return Math.Abs(a.wPanPos - b.wPanPos) < 2 &&
               Math.Abs(a.wTiltPos - b.wTiltPos) < 2 &&
               Math.Abs(a.wZoomPos - b.wZoomPos) < 2;
    }

    public static ushort ConvertAngleToCameraScale(float angle)
    {
        float roundedAngle = (float)Math.Round(angle);

        string angleString = Convert.ToString(Convert.ToUInt32(roundedAngle) * 10);

        return (ushort)Convert.ToUInt16(angleString, 16);
    }

    public static async Task<dynamic> CapturePhoto(string settingId = null, string index = null)
    {
        if (userID == -1)
        {
            var reconnected = ReconnectToCamera();
            if (reconnected == false)
            {
                return "User is not logged In";
            }
        }
        ResetTimer();

        var ms = new MemoryStream();
        string jpgFileName = "";
        const double blurThreshold = 250.0; //a legtöbb kép 550 kb. legkissebb 215-280
        const int maxAttempts = 3;

        CHCNetSDK.NET_DVR_JPEGPARA lpJpegPara = new CHCNetSDK.NET_DVR_JPEGPARA
        {
            wPicQuality = 0,
            wPicSize = 0xff
        };

        int attempt = 0;
        bool isSharp = false;

        while (attempt < maxAttempts && !isSharp)
        {
            attempt++;
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            jpgFileName = Path.Combine(Path.GetTempPath(), $"JPEG_{timestamp}.jpg");

            bool captureSuccess = await Task.Run(() => CHCNetSDK.NET_DVR_CaptureJPEGPicture(userID, 1, ref lpJpegPara, jpgFileName));

            if (!captureSuccess)
            {
                string errorMessage = GetErrorMessage();
                return $"Capture failed: {errorMessage}";
            }

            // --- OpenCV Sharpness Check ---
            using (Mat img = Cv2.ImRead(jpgFileName, ImreadModes.Grayscale))
            {
                if (img.Empty())
                    return "Failed to read captured image for sharpness check.";

                Mat laplacian = new Mat();
                Cv2.Laplacian(img, laplacian, MatType.CV_64F);

                Scalar mean, stddev;
                Cv2.MeanStdDev(laplacian, out mean, out stddev);
                double variance = stddev.Val0 * stddev.Val0;

                if (variance >= blurThreshold)
                {
                    isSharp = true;
                }
                else
                {
                    if(attempt == maxAttempts-1)
                    {
                        await RePosition();
                    }
                    await Task.Delay(500);
                }
            }

            if (!isSharp && attempt < maxAttempts)
            {
                File.Delete(jpgFileName); // töröljük a homályos képet
            }
        }

        try
        {
            using (Image img = Image.Load(jpgFileName))
            {
                if (settingId is not null)
                {
                    var imageMetadata = img.Metadata.ExifProfile;

                    if (imageMetadata == null)
                    {
                        imageMetadata = new ExifProfile();
                        img.Metadata.ExifProfile = imageMetadata;
                    }

                    imageMetadata.SetValue(ExifTag.UserComment, index);
                    imageMetadata.SetValue(ExifTag.Artist, settingId);
                }
                img.Save(ms, new JpegEncoder());
            }
            ms.Position = 0;
        }
        catch (Exception ex)
        {
            return $"Error during PNG conversion: {ex.Message}";
        }
        finally
        {
            File.Delete(jpgFileName);
        }
        return ms;
    }

    private static float ConvertCameraScaleToAngle(ushort cameraValue)
    {
        // A kamera által visszaadott érték hex-ben van (pl. 0x0250)
        // Először alakítsuk vissza stringgé
        string hexString = cameraValue.ToString("X4"); // pl. "0250"

        // Majd ezt olvassuk vissza decimálisan (BCD logikával)
        int decimalValue = int.Parse(hexString);

        // A te eredeti konverziód 10-es szorzót használt, ezért visszaosztjuk
        return decimalValue / 10.0f;
    }

    private static async Task<bool> RePosition()
    {
        try
        {
            if (userID == -1)
            {
                var reconnected = ReconnectToCamera();
                if (!reconnected)
                {
                    return false;
                }
            }

            var currentPos = GetPTZPos(userID, 1);
            if (currentPos.wAction == 0 && currentPos.wPanPos == 0 && currentPos.wTiltPos == 0)
            {
                return false;
            }

            float currentPan = ConvertCameraScaleToAngle(currentPos.wPanPos);
            float currentTilt = ConvertCameraScaleToAngle(currentPos.wTiltPos);
            float currentZoom = (float)currentPos.wZoomPos / oneXZoom;

            var offset = 10.0f; // ennyi fokkal mozdítjuk el a pan-t
            var move1 = new PTZCameraControlItem
            {
                CameraPanPosition = (int)(currentPan + offset),
                CameraTiltPosition = (int)(currentTilt + offset),
                ZoomRatio = (Math.Abs(currentZoom-1) % 5).ToString(CultureInfo.InvariantCulture)
            };

            var move2 = new PTZCameraControlItem
            {
                CameraPanPosition = (int)currentPan,
                CameraTiltPosition = (int)currentTilt,
                ZoomRatio = currentZoom.ToString(CultureInfo.InvariantCulture)
            };

            var result1 = await SetPtzByAngleAndZoom(move1);
            if (result1 != "success")
            {
                return false;
            }

            var result2 = await SetPtzByAngleAndZoom(move2);
            if (result2 != "success")
            {
                return false;
            }

            await Task.Delay(1000); // fókusz stabilizálódási idő

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void AddFile(int userId, MemoryStream stream, string fileName, string[] folders, IConfiguration conf, int stationId, int unitId)
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        string targetDir = "/UploadedFiles";
        FileInfo fileInfo = new FileInfo(fileName);

        if (!Directory.Exists(Path.Combine(basePath, targetDir.TrimStart('/'))))
        {
            Directory.CreateDirectory(Path.Combine(basePath, targetDir.TrimStart('/')));
        }

        foreach (var folder in folders)
        {
            targetDir = Path.Combine(basePath, targetDir.TrimStart('/'), folder);

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
        }

        SqlConnection conn = new SqlConnection(conf.GetConnectionString("DB"));

        try
        {
            conn.Open();

            using (SqlCommand cmd = conn.CreateCommand())
            {
                cmd.CommandType = System.Data.CommandType.StoredProcedure;
                cmd.CommandText = "MES_AddUnitPhoto";
                cmd.Parameters.AddWithValue("@FileName", fileName);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@Extension", fileInfo.Extension);
                cmd.Parameters.AddWithValue("@UnitId", unitId);
                cmd.Parameters.AddWithValue("@StationId", stationId);
                cmd.Parameters.AddWithValue("@Path", targetDir);

                int uploadedFileId = 0;

                using (SqlDataReader rd = cmd.ExecuteReader())
                {
                    if (rd.HasRows)
                    {
                        while (rd.Read())
                        {
                            uploadedFileId = Convert.ToInt32(rd[0]);
                        }
                    }
                }

                if (uploadedFileId > 0)
                {
                    string path = Path.Combine(targetDir, $"{uploadedFileId}{fileInfo.Extension}");

                    using (var fileStream = new FileStream(path, FileMode.Create))
                    {
                        stream.CopyTo(fileStream);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.FileLog(ex.Message, "CameraHelper/AddFile");
        }
        finally
        {
            conn.Close();
        }
    }

    private static void AddFile(int userId, MemoryStream stream, string fileName, string[] folders, DatabaseContext db, IWebHostEnvironment env, string stationId, int unitId)
    {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        string targetDir = "UploadedFiles";
        FileInfo fileInfo = new FileInfo(fileName);
        string path = Path.Combine(basePath, targetDir);

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        foreach (var folder in folders)
        {
            path = Path.Combine(path, folder);

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        UploadedFile uploadedFile = new UploadedFile
        {
            UserId = userId,
            FileName = fileName,
            InsertDate = DateTime.Now
        };

        db.UploadedFiles.Add(uploadedFile);
        db.SaveChanges();
        path = Path.Combine(path, $"{uploadedFile.Id}{fileInfo.Extension}");

        uploadedFile.Path = path;

        db.UnitImages.Add(new .Data.Entities.MES.UnitImage
        {
            UnitId = unitId,
            StationId = Convert.ToInt32(stationId),
            UploadedFileId = uploadedFile.Id,
            InsertDate = DateTime.Now
        });
        db.SaveChanges();

        using (var fileStream = new FileStream(path, FileMode.Create))
        {
            stream.CopyTo(fileStream);
        }
    }

    public static async Task<string> TakePictureAtPosition(PTZCameraControlItem CameraMoves, int UserId, string imageName, DatabaseContext db, IWebHostEnvironment env, string settingId, string stationId, int unitId)
    {
        var zoomResult = SetPtzByAngleAndZoom(CameraMoves, true);
        Unit unit = db.Units.Find(unitId);

        await Task.Delay(1000);
        var photoResult = await CapturePhoto(settingId, imageName.Split("_")[1]);
        if (photoResult is MemoryStream)
        {
            var creationDate = unit != null ? unit.CreationTime : DateTime.Now;
            AddFile(UserId, photoResult, $"{imageName}.jpg", new string[] { creationDate.Year.ToString(), creationDate.Month.ToString("D2"), db.UnitSerialNumbers.SingleOrDefault(c => c.UnitId == unitId)?.Value+"/Photos" ?? "" }, db, env, stationId, unitId);
        }
        else
        {
            return "false";
        }
        return "success";
    }

    public static Task<string> GoToPosition(PTZCameraControlItem cameraMoves)
    {
        string result = SetPtzByAngleAndZoom(cameraMoves, true).GetAwaiter().GetResult();
        return Task.FromResult(result);
    }

    public static string GetErrorMessage()
    {
        switch (CHCNetSDK.NET_DVR_GetLastError())
        {
            case 0: return "NET_DVR_NOERROR: No error";
            case 1: return "NET_DVR_PASSWORD_ERROR: Incorrect password";
            case 2: return "NET_DVR_NOENOUGHPRI: Insufficient permissions";
            case 3: return "NET_DVR_NOINIT: Not initialized";
            case 4: return "NET_DVR_CHANNEL_ERROR: Channel error";
            case 5: return "NET_DVR_OVER_MAXLINK: Exceeded maximum connections";
            case 6: return "NET_DVR_VERSIONNOMATCH: Version mismatch";
            case 7: return "NET_DVR_NETWORK_FAIL_CONNECT: Network connection failed";
            case 8: return "NET_DVR_NETWORK_SEND_ERROR: Network send error";
            case 9: return "NET_DVR_NETWORK_RECV_ERROR: Network receive error";
            case 10: return "NET_DVR_NETWORK_RECV_TIMEOUT: Network receive timeout";
            case 11: return "NET_DVR_NETWORK_ERRORDATA: Invalid network data";
            case 12: return "NET_DVR_ORDER_ERROR: Order error";
            case 13: return "NET_DVR_OPERNOPERMIT: Operation not permitted";
            case 14: return "NET_DVR_COMMANDTIMEOUT: Command timeout";
            case 15: return "NET_DVR_ERRORSERIALPORT: Serial port error";
            case 16: return "NET_DVR_ERRORALARMPORT: Alarm port error";
            case 17: return "NET_DVR_PARAMETER_ERROR: Parameter error";
            case 18: return "NET_DVR_CHAN_EXCEPTION: Channel exception";
            case 19: return "NET_DVR_NODISK: No disk available";
            case 20: return "NET_DVR_ERRORDISKNUM: Disk number error";
            case 21: return "NET_DVR_DISK_FULL: Disk is full";
            case 22: return "NET_DVR_DISK_ERROR: Disk error";
            case 23: return "NET_DVR_NOSUPPORT: Not supported";
            case 24: return "NET_DVR_BUSY: Device is busy";
            case 25: return "NET_DVR_MODIFY_FAIL: Failed to modify settings";
            case 26: return "NET_DVR_PASSWORD_FORMAT_ERROR: Incorrect password format";
            case 27: return "NET_DVR_DISK_FORMATING: Disk is formatting";
            case 28: return "NET_DVR_DVRNORESOURCE: DVR resources unavailable";
            case 29: return "NET_DVR_DVROPRATEFAILED: DVR operation failed";
            case 30: return "NET_DVR_OPENHOSTSOUND_FAIL: Failed to open host sound";
            case 31: return "NET_DVR_DVRVOICEOPENED: DVR voice already opened";
            case 32: return "NET_DVR_TIMEINPUTERROR: Incorrect time input";
            case 33: return "NET_DVR_NOSPECFILE: No specified file";
            case 34: return "NET_DVR_CREATEFILE_ERROR: Failed to create file";
            case 35: return "NET_DVR_FILEOPENFAIL: Failed to open file";
            case 36: return "NET_DVR_OPERNOTFINISH: Operation not finished";
            case 37: return "NET_DVR_GETPLAYTIMEFAIL: Failed to get play time";
            case 38: return "NET_DVR_PLAYFAIL: Playback failed";
            case 39: return "NET_DVR_FILEFORMAT_ERROR: Incorrect file format";
            case 40: return "NET_DVR_DIR_ERROR: Directory error";
            case 41: return "NET_DVR_ALLOC_RESOURCE_ERROR: Resource allocation error";
            case 42: return "NET_DVR_AUDIO_MODE_ERROR: Audio mode error";
            case 43: return "NET_DVR_NOENOUGH_BUF: Not enough buffer";
            case 44: return "NET_DVR_CREATESOCKET_ERROR: Socket creation error";
            case 45: return "NET_DVR_SETSOCKET_ERROR: Socket setup error";
            case 46: return "NET_DVR_MAX_NUM: Max number reached";
            case 47: return "NET_DVR_USERNOTEXIST: User does not exist";
            case 48: return "NET_DVR_WRITEFLASHERROR: Flash write error";
            case 49: return "NET_DVR_UPGRADEFAIL: DVR upgrade failed";
            case 50: return "NET_DVR_CARDHAVEINIT: Card already initialized";
            case 51: return "NET_DVR_PLAYERFAILED: Player failed";
            case 52: return "NET_DVR_MAX_USERNUM: Max user number reached";
            case 53: return "NET_DVR_GETLOCALIPANDMACFAIL: Failed to get local IP and MAC";
            case 54: return "NET_DVR_NOENCODEING: No encoding on the channel";
            case 55: return "NET_DVR_IPMISMATCH: IP mismatch";
            case 56: return "NET_DVR_MACMISMATCH: MAC mismatch";
            case 57: return "NET_DVR_UPGRADELANGMISMATCH: Upgrade language mismatch";
            case 58: return "NET_DVR_MAX_PLAYERPORT: Max player port reached";
            case 59: return "NET_DVR_NOSPACEBACKUP: No space for backup";
            case 60: return "NET_DVR_NODEVICEBACKUP: No device found for backup";
            case 61: return "NET_DVR_PICTURE_BITS_ERROR: Picture bit error";
            case 62: return "NET_DVR_PICTURE_DIMENSION_ERROR: Picture dimension error";
            case 63: return "NET_DVR_PICTURE_SIZ_ERROR: Picture size error";
            case 64: return "NET_DVR_LOADPLAYERSDKFAILED: Player SDK load failed";
            case 65: return "NET_DVR_LOADPLAYERSDKPROC_ERROR: Player SDK procedure error";
            case 66: return "NET_DVR_LOADDSSDKFAILED: DS SDK load failed";
            case 67: return "NET_DVR_LOADDSSDKPROC_ERROR: DS SDK procedure error";
            case 68: return "NET_DVR_DSSDK_ERROR: DS SDK error";
            case 69: return "NET_DVR_VOICEMONOPOLIZE: Voice monopolized";
            case 70: return "NET_DVR_JOINMULTICASTFAILED: Multicast join failed";
            case 71: return "NET_DVR_CREATEDIR_ERROR: Directory creation failed";
            case 72: return "NET_DVR_BINDSOCKET_ERROR: Socket bind error";
            case 73: return "NET_DVR_SOCKETCLOSE_ERROR: Socket close error";
            case 74: return "NET_DVR_USERID_ISUSING: User ID is already in use";
            case 75: return "NET_DVR_SOCKETLISTEN_ERROR: Socket listen error";
            case 76: return "NET_DVR_PROGRAM_EXCEPTION: Program exception";
            case 77: return "NET_DVR_WRITEFILE_FAILED: File write failed";
            case 78: return "NET_DVR_FORMAT_READONLY: Format read-only error";
            case 79: return "NET_DVR_WITHSAMEUSERNAME: Duplicate username detected";
            case 80: return "NET_DVR_DEVICETYPE_ERROR: Device type error";
            case 81: return "NET_DVR_LANGUAGE_ERROR: Language mismatch";
            case 82: return "NET_DVR_PARAVERSION_ERROR: Parameter version mismatch";
            case 83: return "NET_DVR_IPCHAN_NOTALIVE: IP channel not active";
            case 84: return "NET_DVR_RTSP_SDK_ERROR: RTSP SDK error";
            case 85: return "NET_DVR_CONVERT_SDK_ERROR: Conversion SDK error";
            case 86: return "NET_DVR_IPC_COUNT_OVERFLOW: IPC count overflow";
            default:
                return "Unknown error code";
        }
    }

    public static void Cleanup()
    {
        if (userID == -1)
        {
            return;
        }
        var logOut = NET_DVR_Logout_V30(userID);
        StreamHelper.StopStream();
        if (logOut)
        {
            connectionInfo = null;
            userID = -1;
            NET_DVR_Cleanup();
        }
    }
}