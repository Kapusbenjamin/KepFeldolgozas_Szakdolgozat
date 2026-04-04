using AutoMapper;
using ClosedXML.Excel;
using Ion.Sdk.Ddr;
using Ion.Sdk.Ddr.Extensions;
using Ion.Sdk.Ici;
using Ion.Sdk.Idi;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Opc.Ua;
using SixLabors.ImageSharp;
using Syncfusion.Pdf;
using Syncfusion.XlsIO;
using Syncfusion.XlsIORenderer;
using System.Data;
using System.Net.NetworkInformation;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using static Ion.Sdk.Ici.Channel.BlackBox;
using static Ion.Sdk.Ici.Channel.BlackBox.Message;
using static OpenCvSharp.Stitcher;

namespace Kepfeldolgozas_Szakdolgozat.Services
{
    public class MESService : IMESService
    {
        private DatabaseContext db;
        private IConfiguration configuration;
        private IMapper mapper;
        private ILabelingService labelingService;
        private IWebHostEnvironment env;
        private IFrameworkService frameworkService;
        private IELabelService eLabelService;
        private IMemoryCache cache;
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        private string fileStoragePath = "";
        private IMQTTService mqttService;
        public CommonService commonService;
        private ImageProcessor imageProcessor;

        public MESService(DatabaseContext db, IMQTTService mqttService, IConfiguration configuration, IMapper mapper, ILabelingService labelingService, IWebHostEnvironment env, IFrameworkService frameworkService, IELabelService eLabelService, IMemoryCache cache)
        {
            this.db = db;
            this.configuration = configuration;
            this.mapper = mapper;
            this.labelingService = labelingService;
            this.env = env;
            this.frameworkService = frameworkService;
            this.eLabelService = eLabelService;
            this.cache = cache;
            this.fileStoragePath = GetFileStoragePath();
            this.mqttService = mqttService;
            this.commonService = new CommonService(db, configuration, mapper, env);
            this.imageProcessor = new ImageProcessor(db, mqttService, frameworkService, mapper);
        }

        [GenerateCode("Task")]
        public PTZCameraControlSettingModel GetPTZCamaraControlSetting(int settingId)
        {
            List<PTZCameraControlSetting> settings = db.PTZCameraControlSettings.Where(c => c.UnitRouteElementConfigurationSettingId == settingId).ToList();


            if (settings.Count == 1)
            {
                return mapper.Map<PTZCameraControlSettingModel>(settings[0]);
            }
            else if (settings.Count > 1)
            {
                throw new SYSException(3000013, 3000);
            }
            else
            {
                throw new SYSException(3000014, 3000);
            }
        }

        [GenerateCode("Task")]
        public List<PTZCameraControlItemModel> GetPTZCameraControlItems(int settingId)
        {
            return mapper.Map<List<PTZCameraControlItemModel>>(db.PTZCameraControlItems.Where(c => c.PTZCameraControlSettingId == settingId).ToList());
        }

        [GenerateCode("Task")]
        public List<PTZCameraControlItemModel> PTZCameraControlSelectionItems(int unitRouteElementId, int taskGroupId)
        {
            return mapper.Map<List<PTZCameraControlItemModel>>(db.PTZCameraControlItems.Where(c => c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.Active &&
                                                                            c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.TaskGroupId == taskGroupId &&
                                                                            c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.Task == StationTask.PTZCamera &&
                                                                            c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.UnitRouteElementId == unitRouteElementId));
        }

        [GenerateCode("Task")]
        public PTZCameraControlSettingModel PTZCameraControlSetting(int unitRouteElementId, int taskGroupId)
        {

            return mapper.Map<PTZCameraControlSettingModel>(db.PTZCameraControlSettings.FirstOrDefault(c => c.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.Active &&
                                                                                                                    c.UnitRouteElementConfigurationSetting.TaskGroupId == taskGroupId &&
                                                                                                                    c.UnitRouteElementConfigurationSetting.Task == StationTask.PTZCamera &&
                                                                                                                    c.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.UnitRouteElementId == unitRouteElementId));

        }

        private string GetFileStoragePath()
        {
            ParameterDefinition parameterDefinition = db.ParameterDefinitions.FirstOrDefault(c => c.Name == "File storage path");
            return db.SystemParameters.FirstOrDefault(c => c.ParameterDefinitionId == parameterDefinition.Id)?.Value;
        }

        [GenerateCode("Station")]
        public void TakePhoto(HttpContext context, int stationId, int unitId, int settingId, string imageName)
        {
            UnitSerialNumber unitSerialNumber = db.UnitSerialNumbers.FirstOrDefault(c => c.UnitId == unitId);
            Unit unit = db.Units.Find(unitId);

            if (unitSerialNumber != null)
            {
                MemoryStream stream = HikvisionCameraHelper.CapturePhoto(settingId.ToString(), imageName.Split("_")[1]).Result;
                var creationDate = unit != null ? unit.CreationTime : DateTime.Now;
                UploadedFileModel uploadedFile = frameworkService.AddFileData(context.GetUserId(), stream.ToArray(), imageName, new string[] { creationDate.Year.ToString(), creationDate.Month.ToString("D2"), unitSerialNumber.Value, "Photos" });

                db.UnitImages.Add(new UnitImage()
                {
                    UnitId = unitId,
                    UploadedFileId = uploadedFile.Id,
                    StationId = stationId,
                    InsertDate = DateTime.Now
                });
                db.SaveChanges();
            }
        }

        [GenerateCode("Station")]
        public Task GoToPosition(PTZCameraControlItem position)
        {
            HikvisionCameraHelper.GoToPosition(position).GetAwaiter().GetResult();
            return Task.CompletedTask;
        }

        [GenerateCode("ImageProcessing")]
        public List<ImageProcessResult> ImageProcessing(List<CameraControlItemAndImagePairDescriptor> ptzCameraControlItemAndImagePairs, List<CameraControlItemInspection> allCameraControlItemInspections, double offsetX, double offsetY, double rotation)
        {
            try
            {
                List<ImageProcessResult> processResults = imageProcessor.Process(ptzCameraControlItemAndImagePairs, allCameraControlItemInspections, offsetX, offsetY, rotation, env.IsProduction()).GetAwaiter().GetResult();
                if (processResults.Count > 0)
                {
                    return processResults;
                }
                return new List<ImageProcessResult> { };
            }
            catch (Exception e)
            {
                db.Logs.Add(new Data.Entities.Framework.Log
                {
                    EventType = Data.Enums.Framework.EventType.Error,
                    InsertDate = DateTime.Now,
                    Path = "MES/PtzCamera/ImageProcessing",
                    Value = $"{e.Message}",
                    UserId = -1
                });
                db.SaveChanges();
                return new List<ImageProcessResult>{new ImageProcessResult
                    {
                        ImageToAuthorize = "",
                        UnitImageInspections = new List<UnitImageInspectionModel>(),
                        ResultMessage = new ResultMessage { Success = false, Message = e.Message }
                    }
                };
            }
        }

        [GenerateCode("ImageProcessing")]
        public List<UnitImageInspectionModel> GetUnitImageInspectionsByUnitId(int unitId)
        {
            List<int> unitImageIds = db.UnitImages.Where((u) => u.UnitId == unitId).Select((c) => c.Id).ToList();
            if (unitImageIds.Any())
            {
                List<UnitImageInspection> unitImageInspections = db.UnitImageInspections.Where((c) => unitImageIds.Contains(c.UnitImageId)).ToList();
                return mapper.Map<List<UnitImageInspectionModel>>(unitImageInspections);
            }
            else
            {
                return null;
            }
        }

        [GenerateCode("ImageProcessing")]
        public string TestAngleProcess(string base64String, int ptzcameraControlItemId)
        {
            byte[] bytes = Convert.FromBase64String(base64String.Split(',')[1]);
            List<CameraControlItemInspection> inspections = db.CameraControlItemInspections.Where(ins => ins.PTZCameraControlItemId == ptzcameraControlItemId && ins.Updated).ToList();
            if (inspections.Count > 0)
            {
                imageProcessor.TestAngleProcess(bytes, inspections, ptzcameraControlItemId).GetAwaiter().GetResult();
            }

            return "ok";
        }

        [GenerateCode("ImageProcessing")]
        public string SaveTemplateImage(HttpContext context, string base64String, CameraControlItemInspection inspection)
        {
            byte[] bytes = Convert.FromBase64String(base64String.Split(',')[1]);
            imageProcessor.SaveTemplateImage(bytes, inspection, context).GetAwaiter().GetResult();

            return "ok";
        }

        [GenerateCode("CameraControlItemInspection")]
        public string PositioningProcess(CameraControlItemInspection inspection, int unitId)
        {
            string res = imageProcessor.PositioningProcess(inspection, unitId).GetAwaiter().GetResult();
            return res;
        }

        [GenerateCode("CameraControlItemInspection")]
        public string SetArUcoCorners(CameraControlItemInspection inspection)
        {
            string res = imageProcessor.SetArUcoCorners(inspection).GetAwaiter().GetResult();
            return res;
        }

    }
}