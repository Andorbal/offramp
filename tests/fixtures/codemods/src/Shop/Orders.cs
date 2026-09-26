using System.Data.SqlClient;

namespace Shop
{
    public class Orders
    {
        private readonly string _connectionString;

        public Orders(string connectionString)
        {
            _connectionString = connectionString;
        }

        public int Count()
        {
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand("SELECT COUNT(*) FROM Orders", connection))
            {
                connection.Open();
                return (int)command.ExecuteScalar();
            }
        }
    }
}
