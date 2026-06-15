-- async-db workload: rotate the 5 limit variants, mirroring HttpArena's
-- async-db-{5,10,20,35,50}.raw templates (GET /async-db?min=10&max=50&limit=N).
local limits = {5, 10, 20, 35, 50}
local i = 0
request = function()
  i = i + 1
  return wrk.format("GET", "/async-db?min=10&max=50&limit=" .. limits[(i % 5) + 1])
end
