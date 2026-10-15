// Licence header comment.
using System;
using System.Collections.Generic;

namespace Fixtures.Sample
{
    /// <summary>Greets people.</summary>
    [Serializable]
    public sealed class Greeter<T> : IDisposable
        where T : class
    {
        private readonly string _prefix;
        private int _count;

        /// <summary>Creates a greeter.</summary>
        public Greeter(string prefix)
        {
            _prefix = prefix;
        }

        /// <summary>The number of greetings so far.</summary>
        public int Count => _count;

        /// <summary>Greets someone.</summary>
        /// <param name="name">The name.</param>
        [Obsolete("Use GreetAll.")]
        public string Greet(string name)
        {
            _count++;
            return _prefix + ", " + name;
        }

        public IEnumerable<string> GreetAll(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                yield return Greet(name);
            }
        }

        public void Dispose()
        {
        }

        private enum Mood
        {
            Happy,
            Sad,
        }
    }

    public record Point(int X, int Y);
}
