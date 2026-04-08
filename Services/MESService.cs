using Helpers;
using Models;
using System;
using System.Collections.Generic;

namespace Kepfeldolgozas_Szakdolgozat.Services
{
    public class MESService
    {
        private ImageProcessor imageProcessor;

        public MESService()
        {
            this.imageProcessor = new ImageProcessor();
        }

        //public PTZCameraControlSettingModel GetPTZCamaraControlSetting(int settingId)
        //{
        //    List<PTZCameraControlSetting> settings = db.PTZCameraControlSettings.Where(c => c.UnitRouteElementConfigurationSettingId == settingId).ToList();


        //    if (settings.Count == 1)
        //    {
        //        return mapper.Map<PTZCameraControlSettingModel>(settings[0]);
        //    }
        //    else if (settings.Count > 1)
        //    {
        //        throw new SYSException(3000013, 3000);
        //    }
        //    else
        //    {
        //        throw new SYSException(3000014, 3000);
        //    }
        //}

        //public List<PTZCameraControlItemModel> GetPTZCameraControlItems(int settingId)
        //{
        //    return mapper.Map<List<PTZCameraControlItemModel>>(db.PTZCameraControlItems.Where(c => c.PTZCameraControlSettingId == settingId).ToList());
        //}

        //public List<PTZCameraControlItemModel> PTZCameraControlSelectionItems(int unitRouteElementId, int taskGroupId)
        //{
        //    return mapper.Map<List<PTZCameraControlItemModel>>(db.PTZCameraControlItems.Where(c => c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.Active &&
        //                                                                    c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.TaskGroupId == taskGroupId &&
        //                                                                    c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.Task == StationTask.PTZCamera &&
        //                                                                    c.PTZCameraControlSetting.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.UnitRouteElementId == unitRouteElementId));
        //}

        //public PTZCameraControlSettingModel PTZCameraControlSetting(int unitRouteElementId, int taskGroupId)
        //{

        //    return mapper.Map<PTZCameraControlSettingModel>(db.PTZCameraControlSettings.FirstOrDefault(c => c.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.Active &&
        //                                                                                                            c.UnitRouteElementConfigurationSetting.TaskGroupId == taskGroupId &&
        //                                                                                                            c.UnitRouteElementConfigurationSetting.Task == StationTask.PTZCamera &&
        //                                                                                                            c.UnitRouteElementConfigurationSetting.UnitRouteElementConfiguration.UnitRouteElementId == unitRouteElementId));

        //}

        //private string GetFileStoragePath()
        //{
        //    ParameterDefinition parameterDefinition = db.ParameterDefinitions.FirstOrDefault(c => c.Name == "File storage path");
        //    return db.SystemParameters.FirstOrDefault(c => c.ParameterDefinitionId == parameterDefinition.Id)?.Value;
        //}

        //public void TakePhoto(HttpContext context, int stationId, int unitId, int settingId, string imageName)
        //{
        //    UnitSerialNumber unitSerialNumber = db.UnitSerialNumbers.FirstOrDefault(c => c.UnitId == unitId);
        //    Unit unit = db.Units.Find(unitId);

        //    if (unitSerialNumber != null)
        //    {
        //        MemoryStream stream = HikvisionCameraHelper.CapturePhoto(settingId.ToString(), imageName.Split("_")[1]).Result;
        //        var creationDate = unit != null ? unit.CreationTime : DateTime.Now;
        //        UploadedFileModel uploadedFile = frameworkService.AddFileData(context.GetUserId(), stream.ToArray(), imageName, new string[] { creationDate.Year.ToString(), creationDate.Month.ToString("D2"), unitSerialNumber.Value, "Photos" });

        //        db.UnitImages.Add(new UnitImage()
        //        {
        //            UnitId = unitId,
        //            UploadedFileId = uploadedFile.Id,
        //            StationId = stationId,
        //            InsertDate = DateTime.Now
        //        });
        //        db.SaveChanges();
        //    }
        //}

        //public Task GoToPosition(PTZCameraControlItem position)
        //{
        //    HikvisionCameraHelper.GoToPosition(position).GetAwaiter().GetResult();
        //    return Task.CompletedTask;
        //}

        public List<ImageProcessResultModel> ImageProcessing(List<CameraControlItemAndImagePairModel> ptzCameraControlItemAndImagePairs, List<CameraControlItemInspectionModel> allCameraControlItemInspections, double offsetX, double offsetY, double rotation)
        {
            try
            {
                List<ImageProcessResultModel> processResults = imageProcessor.Process(ptzCameraControlItemAndImagePairs, allCameraControlItemInspections, offsetX, offsetY, rotation).GetAwaiter().GetResult();
                if (processResults.Count > 0)
                {
                    return processResults;
                }
                return new List<ImageProcessResultModel> { };
            }
            catch (Exception e)
            {
                //db.Logs.Add(new Data.Entities.Framework.Log
                //{
                //    EventType = Data.Enums.Framework.EventType.Error,
                //    InsertDate = DateTime.Now,
                //    Path = "MES/PtzCamera/ImageProcessing",
                //    Value = $"{e.Message}",
                //    UserId = -1
                //});
                //db.SaveChanges();
                return new List<ImageProcessResultModel>{new ImageProcessResultModel
                    {
                        ImageToAuthorize = "",
                        UnitImageInspections = new List<UnitImageInspectionModel>(),
                        ResultMessage = new ResultMessageModel { Success = false, Message = e.Message }
                    }
                };
            }
        }

        //public List<UnitImageInspectionModel> GetUnitImageInspectionsByUnitId(int unitId)
        //{
        //    List<int> unitImageIds = db.UnitImages.Where((u) => u.UnitId == unitId).Select((c) => c.Id).ToList();
        //    if (unitImageIds.Any())
        //    {
        //        List<UnitImageInspection> unitImageInspections = db.UnitImageInspections.Where((c) => unitImageIds.Contains(c.UnitImageId)).ToList();
        //        return mapper.Map<List<UnitImageInspectionModel>>(unitImageInspections);
        //    }
        //    else
        //    {
        //        return null;
        //    }
        //}

        //public string TestAngleProcess(string base64String, int ptzcameraControlItemId)
        //{
        //    byte[] bytes = Convert.FromBase64String(base64String.Split(',')[1]);
        //    List<CameraControlItemInspection> inspections = db.CameraControlItemInspections.Where(ins => ins.PTZCameraControlItemId == ptzcameraControlItemId && ins.Updated).ToList();
        //    if (inspections.Count > 0)
        //    {
        //        imageProcessor.TestAngleProcess(bytes, inspections, ptzcameraControlItemId).GetAwaiter().GetResult();
        //    }

        //    return "ok";
        //}

        //public string SaveTemplateImage(HttpContext context, string base64String, CameraControlItemInspection inspection)
        //{
        //    byte[] bytes = Convert.FromBase64String(base64String.Split(',')[1]);
        //    imageProcessor.SaveTemplateImage(bytes, inspection, context).GetAwaiter().GetResult();

        //    return "ok";
        //}

        //public string PositioningProcess(CameraControlItemInspection inspection, int unitId)
        //{
        //    string res = imageProcessor.PositioningProcess(inspection, unitId).GetAwaiter().GetResult();
        //    return res;
        //}

        //public string SetArUcoCorners(CameraControlItemInspection inspection)
        //{
        //    string res = imageProcessor.SetArUcoCorners(inspection).GetAwaiter().GetResult();
        //    return res;
        //}

    }
}