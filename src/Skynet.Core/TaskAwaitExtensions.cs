using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Skynet.Core;

public static class TaskAwaitExtensions
{
        public static ConfiguredTaskAwaitable CAF(this Task task)
        {
                return task.ConfigureAwait(false);
        }

        public static ConfiguredTaskAwaitable<T> CAF<T>(this Task<T> task)
        {
                return task.ConfigureAwait(false);
        }

        public static ConfiguredValueTaskAwaitable CAF(this ValueTask task)
        {
                return task.ConfigureAwait(false);
        }

        public static ConfiguredValueTaskAwaitable<T> CAF<T>(this ValueTask<T> task)
        {
                return task.ConfigureAwait(false);
        }
}
