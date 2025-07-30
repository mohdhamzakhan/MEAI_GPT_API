using MEAI_GPT_API.Service.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MEAI_GPT_API.Controller
{
    [Route("api/[controller]")]
    [ApiController]
    public class ModelsController : ControllerBase
    {
        private readonly IModelManager _modelManager;

        public ModelsController(IModelManager modelManager)
        {
            _modelManager = modelManager;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllModels()
        {
            var models = await _modelManager.GetGenerationModelsAsync();
            return Ok(models.Select(m => m.Name));
        }
    }
}
