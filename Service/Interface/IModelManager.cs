using MEAI_GPT_API.Models;

namespace MEAI_GPT_API.Service.Interface
{
    public interface IModelManager
    {
        Task<List<ModelConfiguration>> DiscoverAvailableModelsAsync();
        Task<ModelConfiguration?> GetModelAsync(string modelName);
        Task<List<ModelConfiguration>> GetEmbeddingModelsAsync();
        Task<List<ModelConfiguration>> GetGenerationModelsAsync();
        Task<bool> ValidateModelAsync(string modelName);
    }
}
