using AutoMapper;
using .Contracts;
using .Data;
using .Data.API.MES;
using .Data.Entities.Framework;
using .Data.Entities.MES;
using .Data.Entities.MES.TaskConfiguration;
using .Data.Entities.MES.TaskConfiguration.PTZCameraControl;
using .Data.Enums.Link;
using .Data.Enums.MES;
using .Data.Models;
using .HostedServices;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Path = System.IO.Path;

namespace .Helpers
{
    public class ImageProcessor
    {
        private DatabaseContext db;
        private IMQTTService mqttService;
        private IFrameworkService frameworkService;
        private IMapper mapper;

        private string pythonExe = @"C:\\Program Files\\Python311\\python.exe";
        private string pythonScriptDirectory = @"PythonScripts";
        private string combinedPath;
        private PythonRunner pythonRunner;

        private static readonly Dictionary<int, CancellationTokenSource> _activeAngleProcesses = new();
        private static readonly object _processLock = new();

        public ImageProcessor(DatabaseContext db, IMQTTService mqttService, IFrameworkService frameworkService, IMapper mapper)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            combinedPath = Path.Combine(basePath, pythonScriptDirectory);
            this.mapper = mapper;
            this.db = db;
            this.mqttService = mqttService;
            this.frameworkService = frameworkService;

            if (!File.Exists(pythonExe))
            {
                pythonExe = "python";
            }

            pythonRunner = new PythonRunner(db, pythonExe);
        }

        public async Task<List<ImageProcessResult>> Process(List<CameraControlItemAndImagePairDescriptor> ptzCameraControlItemAndImagePairs, List<CameraControlItemInspection> inspections, double offsetX, double offsetY, double rotation, bool isProd)
        {
            List<ImageProcessResult> processResults = new List<ImageProcessResult>();

            inspections = inspections.Where(i => i.Active 
                                                && i.InspectionType != InspectionType.Position 
                                                && i.InspectionType != InspectionType.PositionWithArUco).ToList();
            var controlItemIds = ptzCameraControlItemAndImagePairs
                                    .Select(a => a.PtzCameraControlItemId)
                                    .Distinct().ToList();
            List<PTZCameraControlItem> controlItems = db.PTZCameraControlItems
                                                        .Where(c => controlItemIds.Contains(c.Id))
                                                        .AsNoTracking().ToList();
            List<CameraControlItemInspection> positioningInspections = db.CameraControlItemInspections.Include(c => c.PTZCameraControlItem)
                                                                        .Where(d => d.InspectionType == InspectionType.Position
                                                                            || d.InspectionType == InspectionType.PositionWithArUco).AsNoTracking().ToList();
            PTZCameraControlItem positionControlItem = positioningInspections.Select(d => d.PTZCameraControlItem)
                                                        .Where(c => c.PartId == controlItems.First().PartId)
                                                        .OrderBy(c => c.Sequence).FirstOrDefault();
            
            int totalCount = inspections.Count;
            var donePerType = new Dictionary<InspectionType, int>();

            foreach(InspectionType insType in inspections.Select(c => c.InspectionType).Distinct())
            {
                List<UnitImageInspection> unitImagesInspections = new List<UnitImageInspection>();

                var parameters = new Dictionary<string, object>
                {
                    ["offsetX"] = offsetX.ToString(CultureInfo.InvariantCulture),
                    ["offsetY"] = offsetY.ToString(CultureInfo.InvariantCulture),
                    ["rotation"] = rotation.ToString(CultureInfo.InvariantCulture),
                    ["p0"] = positionControlItem?.CameraPanPosition ?? 0,
                    ["t0"] = positionControlItem?.CameraTiltPosition ?? 0,
                    ["z0"] = positionControlItem?.ZoomRatio ?? "0",
                    ["isProd"] = isProd
                };
                var batch = new List<Dictionary<string, object>>();

                foreach (var inspection in inspections.Where(i => i.InspectionType == insType))
                {
                    totalCount--;
                    PTZCameraControlItem controlItem = controlItems.FirstOrDefault(c => c.Id == inspection.PTZCameraControlItemId);
                    if(controlItem is null) continue;
                    
                    UploadedFile uploadedFile = db.UploadedFiles.Find(ptzCameraControlItemAndImagePairs.FirstOrDefault(c => c.PtzCameraControlItemId == controlItem.Id).UploadedFileId);
                    if(uploadedFile is null) continue;
                    
                    UnitImage unitImage = db.UnitImages.Where((c) => c.UploadedFileId == uploadedFile.Id).FirstOrDefault(); //kell a UnitImageId, hogy lehessen létrehozni rekordot a UnitImageInspections táblába
                    if(unitImage is null) continue;
                    totalCount++;

                    string path = GetFileStoragePath() + uploadedFile.Path;
                    if (!(Path.Exists(path) || File.Exists(path)))
                    {
                        path = uploadedFile.Path;
                    }
            
                    var itemParameters = await CreateBatchItemParameters(unitImage.UnitId.ToString(), inspection, path);
                    itemParameters.Add("unitId", unitImage.UnitId);
                    itemParameters.Add("unitImageId", unitImage.Id);
                    itemParameters.Add("p1", controlItem.CameraPanPosition);
                    itemParameters.Add("t1", controlItem.CameraTiltPosition);
                    itemParameters.Add("z1", controlItem.ZoomRatio);
                    
                    batch.Add(itemParameters);
                }
                parameters.Add("batch", batch);

                string scriptPath = Path.Combine(combinedPath, insType + ".py");
                JsonElement res = await pythonRunner.Run(parameters, scriptPath, (done, total) =>
                {
                    donePerType[insType] = done;
                    var globalDone = donePerType.Values.Sum();
                    var percent = globalDone * 100.0 / totalCount;
                    mqttService.Publish(
                        SystemTopic.MES,
                        "CameraTaskProgress",
                        Math.Round(percent).ToString()
                    );
                });

                try
                {
                    var resultsElement = CheckResult(res);
                    
                    foreach (var item in resultsElement.EnumerateArray())
                    {
                        var inspectionId = item.GetProperty("inspectionId").GetInt32();
                        var itemSuccess  = item.GetProperty("success").GetBoolean();
                        var result  = item.GetProperty("result").GetBoolean();
                        var score        = item.GetProperty("score").GetDouble();
                        var value        = item.GetProperty("value").ToString();
                        var insertDate   = item.GetProperty("insertDate").GetInt64();
                        var error   = itemSuccess ? "" : item.GetProperty("error").GetString();
                    
                        var b = batch.FirstOrDefault(c => (int)c["inspectionId"] == inspectionId);
                        UnitImageInspection newUnitImageInspection = new UnitImageInspection
                        {
                            Id = 0,
                            UnitId = (int)b["unitId"],
                            UnitImageId = (int)b["unitImageId"],
                            CameraControlItemInspectionId = inspectionId,
                            InsertDate = DateTimeOffset.FromUnixTimeMilliseconds(insertDate).LocalDateTime,
                            Result = result,
                            Score = Math.Round(score, 2),
                            Value = value,
                            Comment = ""
                        };
                        unitImagesInspections.Add(newUnitImageInspection);

                        // failedTest
                        if (!result)
                        {
                            if(!processResults.Any(c => c.UnitImageId == (int)b["unitImageId"]))
                            {
                                Mat img = Cv2.ImRead(b["originalImage"].ToString());
                                ImageProcessResult ipr = new ImageProcessResult
                                {
                                    UnitImageId = (int)b["unitImageId"],
                                    ImageToAuthorize = Convert.ToBase64String(img.ToBytes()),
                                    UnitImageInspections = new List<UnitImageInspectionModel>(),
                                    ResultMessage = new ResultMessage
                                    {
                                        Success = true,
                                        Message = "Needs to be authorized"
                                    }
                                };
                                processResults.Add(ipr);
                            }
                        }

                        // if there is an python error, log it
                        if (!itemSuccess && !string.IsNullOrWhiteSpace(error))
                        {
                            db.Logs.Add(new Log
                            {
                                EventType = Data.Enums.Framework.EventType.Error,
                                InsertDate = DateTime.Now,
                                Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/" + insType,
                                Value = $"UnitId: {(int)b["unitImageId"]} InspectionId: {inspectionId}, Error: {error}",
                                UserId = -1
                            });
                            db.SaveChanges();
                        }
                    }

                    db.UnitImageInspections.AddRange(unitImagesInspections);
                    db.SaveChanges();
                    foreach(var pri in processResults)
                    {
                        pri.UnitImageInspections.AddRange(mapper.Map<List<UnitImageInspectionModel>>(unitImagesInspections.Where(c => c.UnitImageId == pri.UnitImageId)).ToList());
                    }
                }
                catch(Exception e)
                {
                    db.Logs.Add(new Log
                    {
                        EventType = Data.Enums.Framework.EventType.Error,
                        InsertDate = DateTime.Now,
                        Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/" + insType,
                        Value = e.Message,
                        UserId = -1
                    });
                    db.SaveChanges();
                }
            }

            if(processResults.Count == 0)
            {
                ImageProcessResult pri = new ImageProcessResult
                {
                    ImageToAuthorize = "",
                    UnitImageInspections = new List<UnitImageInspectionModel>(),
                    ResultMessage = new ResultMessage
                    {
                        Success = true,
                        Message = "Evry inspection was true."
                    }
                };
                processResults.Add(pri);
            }

            return processResults;
        }

        public async Task TestAngleProcess(byte[] byteImage, List<CameraControlItemInspection> inspections, int ptzId)
        {
            CancellationTokenSource cts;

            lock (_processLock)
            {
                if (_activeAngleProcesses.TryGetValue(ptzId, out var existingCts))
                {
                    existingCts.Cancel();
                    existingCts.Dispose();
                }

                cts = new CancellationTokenSource();
                _activeAngleProcesses[ptzId] = cts;
            }

            try
            {
                string base64Image = Convert.ToBase64String(byteImage);
                inspections = inspections.Where(i => i.InspectionType == InspectionType.Text 
                                                || i.InspectionType == InspectionType.Barcode).ToList();

                foreach(InspectionType insType in inspections.Select(c => c.InspectionType).Distinct())
                {
                    if (cts.Token.IsCancellationRequested) return;

                    var parameters = new Dictionary<string, object>();
                    var batch = new List<Dictionary<string, object>>();

                    foreach (var inspection in inspections.Where(i => i.InspectionType == insType))
                    {
                        var itemParameters = await CreateBatchItemParameters("testAngle", inspection, base64Image);
                        batch.Add(itemParameters);
                    }
                    parameters.Add("batch", batch);

                    if (cts.Token.IsCancellationRequested) return;

                    string scriptPath = Path.Combine(combinedPath, insType + ".py");
                    JsonElement res = await pythonRunner.Run(parameters, scriptPath);

                    if (cts.Token.IsCancellationRequested) return;
                    
                    var resultsElement = new JsonElement();
                    try
                    {
                        resultsElement = CheckResult(res);
                    }
                    catch(Exception e)
                    {
                        db.Logs.Add(new Log
                        {
                            EventType = Data.Enums.Framework.EventType.Error,
                            InsertDate = DateTime.Now,
                            Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/TestAngleProcess",
                            Value = e.Message,
                            UserId = -1
                        });
                        db.SaveChanges();
                        return;
                    }

                    foreach (var item in resultsElement.EnumerateArray())
                    {
                        var inspectionId = item.GetProperty("inspectionId").GetInt32();
                        var itemSuccess = item.GetProperty("success").GetBoolean();
                        var result = item.GetProperty("result").GetBoolean();
                        var score = item.GetProperty("score").GetDouble();
                        var value = item.GetProperty("value").ToString();
                        var angle = item.GetProperty("angle").GetInt32();
                        var insertDate = item.GetProperty("insertDate").GetInt64();
                        var error = itemSuccess ? "" : item.GetProperty("error").GetString();

                        // if there is an python error, log it
                        if (!itemSuccess && !string.IsNullOrWhiteSpace(error))
                        {
                            db.Logs.Add(new Log
                            {
                                EventType = Data.Enums.Framework.EventType.Error,
                                InsertDate = DateTime.Now,
                                Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/TestAngleProcess",
                                Value = $"InspectionId: {inspectionId}, Error: {error}",
                                UserId = -1
                            });
                            await db.SaveChangesAsync();
                        }

                        CameraControlItemInspection inspection = inspections.FirstOrDefault(i => i.Id == inspectionId);
                        if(inspection is null) continue;
                    
                        inspection.Angle = angle;
                        inspection.Updated = false;

                        db.CameraControlItemInspections.Update(inspection);
                    }
                    
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        db.Logs.Add(new Data.Entities.Framework.Log
                        {
                            EventType = Data.Enums.Framework.EventType.Error,
                            InsertDate = DateTime.Now,
                            Path = "MES/PtzCamera/ImageProcessing/TestAngleProcess",
                            Value = ex.Message,
                            UserId = -1
                        });
                        await db.SaveChangesAsync();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                db.Logs.Add(new Data.Entities.Framework.Log
                {
                    EventType = Data.Enums.Framework.EventType.Info,
                    InsertDate = DateTime.Now,
                    Path = "MES/PtzCamera/ImageProcessing/TestAngleProcess",
                    Value = "TestAngleProcess called again for the same PTZcameraControlItemId. The first request cancelled.",
                    UserId = -1
                });
                await db.SaveChangesAsync();
            }
            finally
            {
                lock (_processLock)
                {
                    if (_activeAngleProcesses.TryGetValue(ptzId, out var currentCts) && currentCts == cts)
                    {
                        _activeAngleProcesses.Remove(ptzId);
                    }
                }
            }
        }

        public async Task SaveTemplateImage(byte[] byteImage, CameraControlItemInspection inspection, HttpContext context)
        {
            Mat src = Cv2.ImDecode(byteImage, ImreadModes.Color);

            CameraControlItemInspection scaledInspection = scaleInspectionProps(inspection, src);

            // Kivágás
            double x = scaledInspection.X;
            double y = scaledInspection.Y;
            double width = scaledInspection.Width;
            double height = scaledInspection.Height;
            double offset = scaledInspection.Offset;

            double startX = x - offset;
            double startY = y - offset;
            double endX = x + width + offset;
            double endY = y + height + offset;

            if (startX < 0) startX = 0;
            if (startY < 0) startY = 0;
            if (endX > src.Width) endX = src.Width;
            if (endY > src.Height) endY = src.Height;

            Rect roi = new Rect((int)startX, (int)startY, (int)(endX - startX), (int)(endY - startY));
            Mat cropped = new Mat(src, roi);
            byte[] croppedBytes = cropped.ImEncode(".jpg");

            if (inspection.UploadedFileId != 0)
            {
                frameworkService.Remove(context, inspection.UploadedFileId);
            }

            UploadedFileModel uploadedFile = frameworkService.AddFileData(context.GetUserId(), croppedBytes, "Template_image_inspectionId_" + inspection.Id.ToString() + ".jpg", ["MES", "TemplateImages"]);
            inspection.Updated = false;
            inspection.UploadedFileId = uploadedFile.Id;
            db.CameraControlItemInspections.Update(inspection);
            await db.SaveChangesAsync();
        }

        public async Task<string> PositioningProcess(CameraControlItemInspection inspection, int unitId)
        {
            try
            {
                MemoryStream ms = await HikvisionCameraHelper.CapturePhoto();
                byte[] byteImage = ms.ToArray();
                if (byteImage == null || byteImage.Length == 0) return "";
                string base64Image = Convert.ToBase64String(byteImage);

                var parameters = new Dictionary<string, object>();
                var batch = new List<Dictionary<string, object>>();

                var itemParameters = await CreateBatchItemParameters(unitId.ToString(), inspection, base64Image);
                batch.Add(itemParameters);
                parameters.Add("isProd", true);
                parameters.Add("batch", batch);

                string scriptPath = Path.Combine(combinedPath, inspection.InspectionType + ".py");
                JsonElement res = await pythonRunner.Run(parameters, scriptPath);
                
                var resultsElement = new JsonElement();
                try
                {
                    resultsElement = CheckResult(res);
                }
                catch(Exception e)
                {
                    db.Logs.Add(new Log
                    {
                        EventType = Data.Enums.Framework.EventType.Error,
                        InsertDate = DateTime.Now,
                        Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/Positioning",
                        Value = e.Message,
                        UserId = -1
                    });
                    db.SaveChanges();
                    return "";
                }

                foreach (var item in resultsElement.EnumerateArray())
                {
                    var inspectionId = item.GetProperty("inspectionId").GetInt32();
                    var itemSuccess = item.GetProperty("success").GetBoolean();
                    var result = item.GetProperty("result").GetBoolean();
                    var score = item.GetProperty("score").GetDouble();
                    var value = item.GetProperty("value").ToString();
                    var insertDate = item.GetProperty("insertDate").GetInt64();
                    var error = itemSuccess ? "" : item.GetProperty("error").GetString();

                    // if there is an python error, log it
                    if (!itemSuccess && !string.IsNullOrWhiteSpace(error))
                    {
                        db.Logs.Add(new Log
                        {
                            EventType = Data.Enums.Framework.EventType.Error,
                            InsertDate = DateTime.Now,
                            Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/Positioning",
                            Value = $"UnitId: {unitId}, InspectionId: {inspectionId}, Error: {error}",
                            UserId = -1
                        });
                        await db.SaveChangesAsync();
                    }

                    return value;
                }
            }
            catch (Exception ex)
            {
                db.Logs.Add(new Data.Entities.Framework.Log
                {
                    EventType = Data.Enums.Framework.EventType.Error,
                    InsertDate = DateTime.Now,
                    Path = "MES/PtzCamera/ImageProcessing/Positioning",
                    Value = ex.Message,
                    UserId = -1
                });
                await db.SaveChangesAsync();
                return "";
            }
            return "";
        }

        public async Task<string> SetArUcoCorners(CameraControlItemInspection inspection)
        {
            try
            {
                MemoryStream ms = await HikvisionCameraHelper.CapturePhoto();
                byte[] byteImage = ms.ToArray();
                if (byteImage == null || byteImage.Length == 0) return "";
                string base64Image = Convert.ToBase64String(byteImage);

                var parameters = new Dictionary<string, object>();
                var batch = new List<Dictionary<string, object>>();

                var itemParameters = await CreateBatchItemParameters("PositionWithArUco", inspection, base64Image);
                batch.Add(itemParameters);
                parameters.Add("isProd", true);
                parameters.Add("batch", batch);

                string scriptPath = Path.Combine(combinedPath, inspection.InspectionType + ".py");
                JsonElement res = await pythonRunner.Run(parameters, scriptPath);
                
                var resultsElement = new JsonElement();
                try
                {
                    resultsElement = CheckResult(res);
                }
                catch(Exception e)
                {
                    db.Logs.Add(new Log
                    {
                        EventType = Data.Enums.Framework.EventType.Error,
                        InsertDate = DateTime.Now,
                        Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/Positioning",
                        Value = e.Message,
                        UserId = -1
                    });
                    db.SaveChanges();
                    return "";
                }

                foreach (var item in resultsElement.EnumerateArray())
                {
                    var inspectionId = item.GetProperty("inspectionId").GetInt32();
                    var itemSuccess = item.GetProperty("success").GetBoolean();
                    var result = item.GetProperty("result").GetBoolean();
                    var score = item.GetProperty("score").GetDouble();
                    var value = item.GetProperty("value");
                    var insertDate = item.GetProperty("insertDate").GetInt64();
                    var error = itemSuccess ? "" : item.GetProperty("error").GetString();

                    // if there is an python error, log it
                    if (!itemSuccess && !string.IsNullOrWhiteSpace(error))
                    {
                        db.Logs.Add(new Log
                        {
                            EventType = Data.Enums.Framework.EventType.Error,
                            InsertDate = DateTime.Now,
                            Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/Positioning",
                            Value = $"InspectionId: {inspectionId}, Error: {error}",
                            UserId = -1
                        });
                        await db.SaveChangesAsync();
                    }

                    if (inspection.InspectionType == InspectionType.PositionWithArUco)
                    {
                        if (value.ValueKind == JsonValueKind.Object &&
                            value.TryGetProperty("templateDetails", out var templateDetails))
                        {
                            var corners = templateDetails.GetProperty("corners").EnumerateArray().ToList();

                            inspection.ArUcoCorner1X = (int)Math.Round(corners[0].GetProperty("x").GetDouble());
                            inspection.ArUcoCorner1Y = (int)Math.Round(corners[0].GetProperty("y").GetDouble());

                            inspection.ArUcoCorner2X = (int)Math.Round(corners[1].GetProperty("x").GetDouble());
                            inspection.ArUcoCorner2Y = (int)Math.Round(corners[1].GetProperty("y").GetDouble());

                            inspection.ArUcoCorner3X = (int)Math.Round(corners[2].GetProperty("x").GetDouble());
                            inspection.ArUcoCorner3Y = (int)Math.Round(corners[2].GetProperty("y").GetDouble());

                            inspection.ArUcoCorner4X = (int)Math.Round(corners[3].GetProperty("x").GetDouble());
                            inspection.ArUcoCorner4Y = (int)Math.Round(corners[3].GetProperty("y").GetDouble());

                            db.CameraControlItemInspections.Update(inspection);
                            db.SaveChanges();
                        }
                    }
                    return value.ToString();
                }

            }
            catch (Exception ex)
            {
                db.Logs.Add(new Data.Entities.Framework.Log
                {
                    EventType = Data.Enums.Framework.EventType.Error,
                    InsertDate = DateTime.Now,
                    Path = "MES/PtzCamera/ImageProcessing/Positioning",
                    Value = ex.Message,
                    UserId = -1
                });
                await db.SaveChangesAsync();
                return "";
            }
            return "";
        }

        private JsonElement CheckResult(JsonElement res)
        {
            if (res.ValueKind != JsonValueKind.Object)
            {
                string errorString = "Error during python script result read: " + res.ToString();
                throw new Exception(errorString);
            }

            bool success = res.GetProperty("success").GetBoolean();
            if (!success)
            {
                var error = res.GetProperty("error").GetString();
                string errorString = "Error during python script run: " + error;
                throw new Exception(errorString);
            }
            
            var resultsElement = res.GetProperty("results");
            if (resultsElement.ValueKind != JsonValueKind.Array)
            {
                string errorString = "Python response 'results' is not an array";
                throw new Exception(errorString);
            }

            return resultsElement;
        }

        private async Task<Dictionary<string, object>> CreateBatchItemParameters(string logId, CameraControlItemInspection inspection, string imagePath)
        {
            DateTime now = DateTime.Now;
            string formattedDate = now.ToString("yyyyMMdd");
            string logName = logId + "_" + inspection.Id + "_" + Guid.NewGuid().ToString().Substring(0, 4);
            string outputPath = GetFileStoragePath() == "" 
                                ? Path.Combine(combinedPath, "debug", formattedDate, logName + "_output.json") 
                                : Path.Combine(GetFileStoragePath(), "UploadedFiles", "MES", inspection.InspectionType + "Images", "debug", formattedDate, logName + "_output.json");

            var itemParameters = new Dictionary<string, object>
            {
                ["inspectionId"] = inspection.Id,
                ["logName"] = logName,
                ["requiredValue"] = inspection.RequiredValue,
                ["angle"] = inspection.Angle,
                ["output"] = outputPath,
                ["originalImage"] = imagePath,
                ["inspectionX"] = (int)inspection.X,
                ["inspectionY"] = (int)inspection.Y,
                ["inspectionWidth"] = (int)inspection.Width,
                ["inspectionHeight"] = (int)inspection.Height,
                ["inspectionOffset"] = inspection.Offset,
                ["dimensionX"] = inspection.DimensionX,
                ["dimensionY"] = inspection.DimensionY
            };

            if (inspection.UploadedFileId != 0)
            {
                UploadedFile templateFile = await db.UploadedFiles.FirstOrDefaultAsync(f => f.Id == inspection.UploadedFileId);
                if (templateFile != null)
                {
                    string templatePath = GetFileStoragePath() + templateFile.Path;
                    if (!(Path.Exists(templatePath) || File.Exists(templatePath)))
                    {
                        templatePath = templateFile.Path;
                    }
                    itemParameters.Add("templateImage", templatePath);
                }
            }

            return itemParameters;
        }

        private CameraControlItemInspection scaleInspectionProps(CameraControlItemInspection inspection, Mat image)
        {
            double scaleX = (double)image.Width / inspection.DimensionX;
            double scaleY = (double)image.Height / inspection.DimensionY;

            double scaledX = inspection.X * scaleX;
            double scaledY = inspection.Y * scaleY;
            double scaledWidth = inspection.Width * scaleX;
            double scaledHeight = inspection.Height * scaleY;

            if (scaleX < 0) scaledX = 0;
            if (scaleY < 0) scaledY = 0;
            if (scaledX + scaledWidth > image.Width) scaledWidth = image.Width - scaledX;
            if (scaledY + scaledHeight > image.Height) scaledHeight = image.Height - scaledY;

            CameraControlItemInspection scaledInspection = new CameraControlItemInspection
            {
                X = scaledX,
                Y = scaledY,
                Width = scaledWidth,
                Height = scaledHeight,
                Offset = inspection.Offset,
                InitialXOffset = inspection.InitialXOffset,
                InitialYOffset = inspection.InitialYOffset,
                DimensionX = image.Width,
                DimensionY = image.Height,
                InspectionType = inspection.InspectionType,
                Id = inspection.Id,
                PTZCameraControlItemId = inspection.PTZCameraControlItemId,
                Updated = inspection.Updated
            };
            return scaledInspection;
        }

        private string GetFileStoragePath()
        {
            ParameterDefinition parameterDefinition = db.ParameterDefinitions.FirstOrDefault(c => c.Name == "File storage path");
            var item = db.SystemParameters.FirstOrDefault(c => c.ParameterDefinitionId == parameterDefinition.Id)?.Value;
            return item;
        }
    }
}
