namespace WisteriaInternAttendance.Models
{
    public class Attendance
    {
        public int Id { get; set; }

        public int UserId { get; set; }

        public DateTime ClockIn { get; set; }

        public DateTime? ClockOut { get; set; }
    }
}
