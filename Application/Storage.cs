using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Azure.Cosmos.Table;

namespace TvShowRss
{
    static class Storage
    {
        internal static IReadOnlyCollection<T> GetAll<T>(string dbFile) where T : ITableEntity, new() => 
            Execute<T, IReadOnlyCollection<T>>(
                dbFile, 
                collection => collection.ExecuteQuery(new TableQuery<T>()).ToList());

        internal static IReadOnlyCollection<T> GetAll<T>(string dbFile, Expression<Func<T, bool>> predicate) where T : ITableEntity, new() => 
            Execute<T, IReadOnlyCollection<T>>(dbFile, collection => collection.CreateQuery<T>().Where(predicate).ToList());

        internal static void Save<T>(string dbFile, T entity) where T : ITableEntity, new() => 
            Execute<T, object>(dbFile, collection => collection.Execute(TableOperation.InsertOrReplace(entity)));

        internal static int SaveAll<T>(string dbFile, IEnumerable<T> entities) where T : ITableEntity, new() => 
            Execute<T, int>(dbFile, collection => entities.Select(entity => collection.Execute(TableOperation.InsertOrReplace(entity))).Count());

        internal static int UpdateAll<T>(string dbFile, IEnumerable<T> entities) where T : ITableEntity, new() => 
            Execute<T, int>(dbFile, collection => entities.Select(entity => collection.Execute(TableOperation.Merge(entity))).Count());
        
        internal static int Delete<T>(string dbFile, Expression<Func<T, bool>> predicate) where T : ITableEntity, new() => 
            Execute<T, int>(dbFile, collection => collection.CreateQuery<T>()
                                                                  .Where(predicate)
                                                                  .Select(entity => 
                    collection.Execute(TableOperation.Delete(entity), null, null))
                                                                  .Count());

        static TReturn Execute<T, TReturn>(string dbFile, Func<CloudTable, TReturn> action) where T : ITableEntity, new() =>
            action(CloudStorageAccount.Parse(dbFile)
                                          .CreateCloudTableClient(new TableClientConfiguration())
                                          .GetTableReference("series"));
    }
}