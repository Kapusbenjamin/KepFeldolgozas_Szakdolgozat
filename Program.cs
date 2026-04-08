using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Helpers;
using Kepfeldolgozas_Szakdolgozat.Services;
using Models;

namespace KepFeldolgozas_Szakdolgozat
{
    class Program
    {
        static void Main(string[] args)
        {
            MESService mesService = new MESService();
            // generate some test data, to call imageprocessing
            var testImagePairs = new List<CameraControlItemAndImagePairModel>
            {
                new CameraControlItemAndImagePairModel { UploadedFilePath = @"C:\Users\Sharkz\Documents\MUNKA\kombo\KepFeldolgozas_Szakdolgozat\30862.jpg", PtzCameraControlItemId = 1 },
                new CameraControlItemAndImagePairModel { UploadedFilePath = @"C:\Users\Sharkz\Documents\MUNKA\kombo\KepFeldolgozas_Szakdolgozat\56204.jpg", PtzCameraControlItemId = 2 }
            };

            var testInspections = new List<CameraControlItemInspectionModel>
            {
                new CameraControlItemInspectionModel { 
                    Id = 1, 
                    ControlItemName = "Test1", 
                    X = 275, 
                    Y = 240, 
                    Width = 450-275, 
                    Height = 360-240, 
                    PTZCameraControlItemId = 1, 
                    UploadedFileId = 101, 
                    Active = true,
                    Angle = 0,
                    DimensionX = 2560,
                    DimensionY = 1440,
                    InspectionType = Enums.InspectionType.Text,
                    RequiredValue = "80A",
                    Offset = 10,
                    OriginalImgHeight = 1440,
                    OriginalImgWidth = 2560
                },
                new CameraControlItemInspectionModel { Id = 2, ControlItemName = "Test2", 
                    X = 1465, Y = 300, Width = 290, Height = 225, PTZCameraControlItemId = 2, UploadedFileId = 102, Active = true,
                    Angle = 0,
                    DimensionX = 2560,
                    DimensionY = 1440,
                    InspectionType = Enums.InspectionType.ScrewTorque,
                    RequiredValue = "",
                    Offset = 10,
                    OriginalImgHeight = 1440,
                    OriginalImgWidth = 2560 },
                new CameraControlItemInspectionModel { Id = 3, ControlItemName = "Test3", 
                    X = 1850, Y = 900, Width = 2070-1850, Height = 1155-900, PTZCameraControlItemId = 2, UploadedFileId = 102, Active = true,
                    Angle = 0,
                    DimensionX = 2560,
                    DimensionY = 1440,
                    InspectionType = Enums.InspectionType.ScrewTorque,
                    RequiredValue = "",
                    Offset = 10,
                    OriginalImgHeight = 1440,
                    OriginalImgWidth = 2560 },
                new CameraControlItemInspectionModel { Id = 4, ControlItemName = "Test4", 
                    X = 30, Y = 40, Width = 120, Height = 120, PTZCameraControlItemId = 1, UploadedFileId = 102, Active = true,
                    Angle = 0,
                    DimensionX = 2560,
                    DimensionY = 1440,
                    InspectionType = Enums.InspectionType.ScrewTorque,
                    RequiredValue = "",
                    Offset = 10,
                    OriginalImgHeight = 1440,
                    OriginalImgWidth = 2560 },
                new CameraControlItemInspectionModel {
                    Id = 5,
                    ControlItemName = "Test5",
                    X = 950,
                    Y = 290,
                    Width = 1105-950,
                    Height = 390-290,
                    PTZCameraControlItemId = 1,
                    UploadedFileId = 101,
                    Active = true,
                    Angle = 0,
                    DimensionX = 2560,
                    DimensionY = 1440,
                    InspectionType = Enums.InspectionType.Text,
                    RequiredValue = "600A",
                    Offset = 10,
                    OriginalImgHeight = 1440,
                    OriginalImgWidth = 2560
                },
                new CameraControlItemInspectionModel {
                    Id = 6,
                    ControlItemName = "Test6",
                    X = 950,
                    Y = 290,
                    Width = 1105-950,
                    Height = 390-290,
                    PTZCameraControlItemId = 1,
                    UploadedFileId = 101,
                    Active = true,
                    Angle = 0,
                    DimensionX = 2560,
                    DimensionY = 1440,
                    InspectionType = Enums.InspectionType.Barcode,
                    RequiredValue = "123BC5D4FWH564",
                    Offset = 10,
                    OriginalImgHeight = 1440,
                    OriginalImgWidth = 2560
                },
            };

            double offsetX = 0.0;
            double offsetY = 0.0;
            double rotation = 0.0;

            var results = mesService.ImageProcessing(testImagePairs, testInspections, offsetX, offsetY, rotation);
        }
    }
}
