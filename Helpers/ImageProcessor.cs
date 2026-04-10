using Enums;
using Models;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Path = System.IO.Path;

namespace Helpers
{
    public class ImageProcessor
    {
        //private string pythonExe = @"C:\\Program Files\\Python311\\python.exe";
        private string pythonScriptDirectory = @"PythonScripts";
        private string combinedPath;
        private string basePath;
        private PythonRunner pythonRunner;

        public ImageProcessor()
        {
            basePath = AppContext.BaseDirectory;
            combinedPath = Path.Combine(basePath, pythonScriptDirectory);

            //if (!File.Exists(pythonExe))
            //{
            //    pythonExe = "python";
            //}

            pythonRunner = new PythonRunner();
        }

        public async Task<List<ImageProcessResultModel>> Process(List<CameraControlItemAndImagePairModel> ptzCameraControlItemAndImagePairs, List<CameraControlItemInspectionModel> inspections, double offsetX, double offsetY, double rotation)
        {
            List<ImageProcessResultModel> processResults = new List<ImageProcessResultModel>();

            inspections = inspections.Where(i => i.Active 
                                                && i.InspectionType != InspectionType.Position 
                                                && i.InspectionType != InspectionType.PositionWithArUco).ToList();
            var controlItemIds = ptzCameraControlItemAndImagePairs
                                    .Select(a => a.PtzCameraControlItemId)
                                    .Distinct().ToList();
            
            int totalCount = inspections.Count;
            var donePerType = new Dictionary<InspectionType, int>();
            int unitId = 25134; // test unitId

            Console.WriteLine("Progress of processed inspections:");
            Console.WriteLine("0 %");

            foreach (InspectionType insType in inspections.Select(c => c.InspectionType).Distinct())
            {
                List<UnitImageInspectionModel> unitImagesInspections = new List<UnitImageInspectionModel>();

                var parameters = new Dictionary<string, object>
                {
                    ["offsetX"] = offsetX.ToString(CultureInfo.InvariantCulture),
                    ["offsetY"] = offsetY.ToString(CultureInfo.InvariantCulture),
                    ["rotation"] = rotation.ToString(CultureInfo.InvariantCulture)
                };
                var batch = new List<Dictionary<string, object>>();

                foreach (var inspection in inspections.Where(i => i.InspectionType == insType))
                {
                    CameraControlItemAndImagePairModel itemAndImagePair = ptzCameraControlItemAndImagePairs
                                    .Where(p => p.PtzCameraControlItemId == inspection.PTZCameraControlItemId)
                                    .FirstOrDefault();

                    var itemParameters = await CreateBatchItemParameters(unitId.ToString(), inspection, itemAndImagePair.UploadedFilePath);
                    itemParameters.Add("unitId", unitId);
                    itemParameters.Add("unitImageId", itemAndImagePair.UnitImageId);

                    batch.Add(itemParameters);
                }
                parameters.Add("batch", batch);

                string scriptPath = Path.Combine(combinedPath, insType + ".py");
                string exePath = Path.Combine(combinedPath, insType.ToString() + ".exe");
                pythonRunner.pythonExe = exePath;
                JsonElement res = await pythonRunner.Run(parameters, scriptPath, (done, total) =>
                {
                    donePerType[insType] = done;
                    var globalDone = donePerType.Values.Sum();
                    var percent = globalDone * 100.0 / totalCount;
                    Console.WriteLine(percent.ToString("F2") + "%");
                    //mqttService.Publish(
                    //    SystemTopic.MES,
                    //    "CameraTaskProgress",
                    //    Math.Round(percent).ToString()
                    //);
                });

                try
                {
                    var resultsElement = CheckResult(res);
                    
                    foreach (var item in resultsElement.EnumerateArray())
                    {
                        var inspectionId = item.GetProperty("inspectionId").GetInt32();
                        var itemSuccess  = item.GetProperty("success").GetBoolean();
                        var result       = item.GetProperty("result").GetBoolean();
                        var score        = item.GetProperty("score").GetDouble();
                        var value        = item.GetProperty("value").ToString();
                        var insertDate   = item.GetProperty("insertDate").GetInt64();
                        var error        = itemSuccess ? "" : item.GetProperty("error").GetString();
                    
                        var b = batch.FirstOrDefault(c => (int)c["inspectionId"] == inspectionId);
                        UnitImageInspectionModel newUnitImageInspection = new UnitImageInspectionModel
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
                        //if (!result)
                        //{
                        if(!processResults.Any(c => c.UnitImageId == (int)b["unitImageId"]))
                        {
                            ImageProcessResultModel ipr = new ImageProcessResultModel
                            {
                                UnitImageId = (int)b["unitImageId"],
                                ImageToAuthorize = b["originalImage"].ToString(),
                                UnitImageInspections = new List<UnitImageInspectionModel>(),
                                ResultMessage = new ResultMessageModel
                                {
                                    Success = true,
                                    Message = "Needs to be authorized"
                                }
                            };
                            processResults.Add(ipr);
                        }
                        //}

                        // if there is an python error, log it
                        if (!itemSuccess && !string.IsNullOrWhiteSpace(error))
                        {
                            Console.WriteLine("Python error:");
                            Console.WriteLine(error);
                            //db.Logs.Add(new Log
                            //{
                            //    EventType = Data.Enums.Framework.EventType.Error,
                            //    InsertDate = DateTime.Now,
                            //    Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/" + insType,
                            //    Value = $"UnitId: {(int)b["unitImageId"]} InspectionId: {inspectionId}, Error: {error}",
                            //    UserId = -1
                            //});
                            //db.SaveChanges();
                        }
                    }

                    //db.UnitImageInspections.AddRange(unitImagesInspections);
                    //db.SaveChanges();
                    foreach(var pri in processResults)
                    {
                        pri.UnitImageInspections.AddRange(unitImagesInspections.Where(c => c.UnitImageId == pri.UnitImageId));
                    }
                }
                catch(Exception e)
                {
                    //db.Logs.Add(new Log
                    //{
                    //    EventType = Data.Enums.Framework.EventType.Error,
                    //    InsertDate = DateTime.Now,
                    //    Path = "MES/PtzCamera/ImageProcessing/ImageProcessor/" + insType,
                    //    Value = e.Message,
                    //    UserId = -1
                    //});
                    //db.SaveChanges();
                }
            }

            if(processResults.Count == 0)
            {
                ImageProcessResultModel pri = new ImageProcessResultModel
                {
                    ImageToAuthorize = "",
                    UnitImageInspections = new List<UnitImageInspectionModel>(),
                    ResultMessage = new ResultMessageModel
                    {
                        Success = true,
                        Message = "Evry inspection was true."
                    }
                };
                processResults.Add(pri);
            }

            return processResults;
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

        private async Task<Dictionary<string, object>> CreateBatchItemParameters(string logId, CameraControlItemInspectionModel inspection, string imagePath)
        {
            DateTime now = DateTime.Now;
            string formattedDate = now.ToString("yyyyMMdd");
            string logName = logId + "_" + inspection.Id + "_" + Guid.NewGuid().ToString().Substring(0, 4);
            string outputPath = Path.Combine("debug", formattedDate, logName + "_output.json");
            var fullPath = Path.GetFullPath(Path.Combine(basePath, imagePath));

            var itemParameters = new Dictionary<string, object>
            {
                ["inspectionId"] = inspection.Id,
                ["logName"] = logName,
                ["requiredValue"] = inspection.RequiredValue,
                ["angle"] = inspection.Angle,
                ["output"] = outputPath,
                ["originalImage"] = fullPath,
                ["inspectionX"] = (int)inspection.X,
                ["inspectionY"] = (int)inspection.Y,
                ["inspectionWidth"] = (int)inspection.Width,
                ["inspectionHeight"] = (int)inspection.Height,
                ["inspectionOffset"] = inspection.Offset,
                ["dimensionX"] = inspection.DimensionX,
                ["dimensionY"] = inspection.DimensionY
            };

            return itemParameters;
        }

    }
}
