using System;
using ServiceStack.DataAnnotations;

namespace JDMonitor.Entity;

public class EntityBase
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public DateTime CreateAt { get; set; } = DateTime.Now;

    [Default(typeof(bool), "false")] public bool IsDeleted { get; set; }
}