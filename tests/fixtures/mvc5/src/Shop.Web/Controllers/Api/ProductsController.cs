using System.Collections.Generic;
using System.Linq;
using System.Web.Http;
using Shop.Web.Models;

namespace Shop.Web.Controllers.Api
{
    [RoutePrefix("api/products")]
    public class ProductsController : ApiController
    {
        private static readonly List<Product> Products = new List<Product>
        {
            new Product { Id = 1, Name = "Kettle", Price = 25m },
            new Product { Id = 2, Name = "Teapot", Price = 18m },
        };

        [Route("")]
        public IEnumerable<Product> Get()
        {
            return Products;
        }

        [Route("{id:int}")]
        public IHttpActionResult Get(int id)
        {
            var product = Products.FirstOrDefault(p => p.Id == id);
            if (product == null)
            {
                return NotFound();
            }

            return Ok(product);
        }

        [Route("")]
        [HttpPost]
        public IHttpActionResult Post(Product product)
        {
            Products.Add(product);
            return Created("api/products/" + product.Id, product);
        }
    }
}
