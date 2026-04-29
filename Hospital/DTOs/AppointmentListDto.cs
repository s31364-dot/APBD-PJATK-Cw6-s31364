namespace Hospital.DTO;

public class AppointmentListDto
{
     public int IdAppointment { get; set; }
     public DateTime AppointmentData { get; set; }
     public string status { get; set; }
     public string PatientFullName  { get; set; }
     public string PatientEmail { get; set; }
     
}