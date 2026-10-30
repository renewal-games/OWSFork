using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace OWSTests.CharacterPersistence
{
    /// <summary>
    /// Stands in for a repository interface: records each call by method name and answers with the reply set for that
    /// method, else an empty one (a completed Task, default result).
    /// </summary>
    public class RepositoryStub<T> : DispatchProxy where T : class
    {
        public ConcurrentQueue<string> Calls { get; } = new ConcurrentQueue<string>();
        public ConcurrentDictionary<string, object> Replies { get; } = new ConcurrentDictionary<string, object>();

        public T Repository => (T)(object)this;

        public static RepositoryStub<T> Create() => (RepositoryStub<T>)(object)Create<T, RepositoryStub<T>>();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Calls.Enqueue(targetMethod.Name);
            Replies.TryGetValue(targetMethod.Name, out object reply);

            Type returnType = targetMethod.ReturnType;
            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type resultType = returnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult)).MakeGenericMethod(resultType)
                    .Invoke(null, new[] { reply ?? DefaultOf(resultType) });
            }
            return reply ?? DefaultOf(returnType);
        }

        private static object DefaultOf(Type type) =>
            type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
