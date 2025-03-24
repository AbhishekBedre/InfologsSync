using Microsoft.Extensions.Caching.Memory;

namespace Infologs.SessionReader
{
    public interface ICacheHelper
    {
        T Get<T>(string cacheKey);
        T GetOrCreate<T>(string cacheKey, Func<T> getData, TimeSpan? expiration = null);
        void Remove(string cacheKey);
    }

    public class CacheHelper : ICacheHelper
    {
        private readonly IMemoryCache _cache;

        public CacheHelper(IMemoryCache cache)
        {
            _cache = cache;
        }

        /// <summary>
        /// Gets data from cache if available, otherwise calls the function to generate data and stores it in cache.
        /// </summary>
        public T GetOrCreate<T>(string cacheKey, Func<T> getData, TimeSpan? expiration = null)
        {
            if (!_cache.TryGetValue(cacheKey, out T cachedValue))
            {
                cachedValue = getData.Invoke();

                var cacheOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(60)// Default: 5 minutes
                };

                _cache.Set(cacheKey, cachedValue, cacheOptions);
            }

            return cachedValue;
        }

        public T Get<T>(string cacheKey)
        {
            T cachedValue;

            if (_cache.TryGetValue(cacheKey, out cachedValue))
            {
                return cachedValue;
            }
            return cachedValue;
        }

        /// <summary>
        /// Removes an item from cache
        /// </summary>
        public void Remove(string cacheKey)
        {
            _cache.Remove(cacheKey);
        }
    }
}
