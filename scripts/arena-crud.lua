-- crud workload: 20-template rotation matching HttpArena's gcannon mix —
-- 15 GET item (75%) / 3 PUT update (15%) / 1 GET list (5%) / 1 POST create (5%),
-- with {RAND:1:50000} ids and a {SEQ:100001} create counter (per wrk thread).
math.randomseed(2026)

local JSON   = {["Content-Type"] = "application/json"}
local UPBODY = '{"name":"Updated Product","category":"test","price":200,"quantity":25}'
local seq    = 100001

local function rid() return math.random(1, 50000) end

local t = {}
for _ = 1, 15 do t[#t + 1] = function() return wrk.format("GET", "/crud/items/" .. rid()) end end
for _ = 1, 3  do t[#t + 1] = function() return wrk.format("PUT", "/crud/items/" .. rid(), JSON, UPBODY) end end
t[#t + 1] = function() return wrk.format("GET", "/crud/items?category=office&page=" .. math.random(1, 10) .. "&limit=10") end
t[#t + 1] = function()
  seq = seq + 1
  return wrk.format("POST", "/crud/items", JSON,
    '{"id":' .. seq .. ',"name":"New Product","category":"test","price":150,"quantity":30}')
end

local i = 0
request = function()
  i = i + 1
  return t[(i % #t) + 1]()
end
