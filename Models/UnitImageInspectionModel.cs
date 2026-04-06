using System;

namespace Models
{
    public class UnitImageInspectionModel
    {
        public int Id { get; set; }
        public int UnitId { get; set; }
        public int UnitImageId { get; set; }
        public int CameraControlItemInspectionId { get; set; }
        public bool Result { get; set; }
        public string Value { get; set; }
        public double Score { get; set; }
        public string Comment { get; set; }
        public DateTime InsertDate { get; set; }
    }
}