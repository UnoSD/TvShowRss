using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Azure.Cosmos.Table;

namespace TvShowRss
{
    [DebuggerDisplay("{" + nameof(Id) + "} - {" + nameof(Name) + "}")]
    class Series : ITableEntity
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public bool IsRunning { get; set; }


        public void ReadEntity(IDictionary<string, EntityProperty> properties, OperationContext operationContext)
        {
            Id = (uint)(properties[nameof(Id)].Int64Value ?? throw new Exception("Id is missing"));
            Name = properties[nameof(Name)].StringValue ?? throw new Exception("Name is missing");
            IsRunning = properties[nameof(IsRunning)].BooleanValue ?? throw new Exception("IsRunning missing");
            PartitionKey = properties.TryGetValue(nameof(PartitionKey), out var pk) ? pk.StringValue : nameof(Series);
            RowKey = properties.TryGetValue(nameof(RowKey), out var rk) ? rk.StringValue : Id.ToString();
            Timestamp = properties.TryGetValue(nameof(Timestamp), out var ts) &&
                        ts.DateTimeOffsetValue.HasValue ?
                        ts.DateTimeOffsetValue.Value : 
                        default;
            ETag = properties.TryGetValue(nameof(ETag), out var t) ? t.StringValue : null;
        }

        public IDictionary<string, EntityProperty> WriteEntity(OperationContext operationContext) =>
            new Dictionary<string, EntityProperty>
            {
                [nameof(Id)] = EntityProperty.GeneratePropertyForLong(Id),
                [nameof(Name)] = EntityProperty.GeneratePropertyForString(Name),
                [nameof(IsRunning)] = EntityProperty.GeneratePropertyForBool(IsRunning),
                [nameof(PartitionKey)] = EntityProperty.GeneratePropertyForString(PartitionKey),
                [nameof(RowKey)] = EntityProperty.GeneratePropertyForString(RowKey),
                [nameof(Timestamp)] = EntityProperty.GeneratePropertyForDateTimeOffset(Timestamp),
                [nameof(ETag)] = EntityProperty.GeneratePropertyForString(ETag)
            };

        public string PartitionKey { get; set; } = nameof(Series);
        
        public string RowKey
        {
            get => Id.ToString();
            set => Id = uint.Parse(value);
        }

        public DateTimeOffset Timestamp { get; set; }
        
        public string ETag { get; set; }
    }
}