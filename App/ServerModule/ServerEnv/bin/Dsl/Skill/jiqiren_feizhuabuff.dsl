skill(116)
{  
  section(2000)
  {
    enablemoveagent(false);
    animation("Stand");
    selfeffect(selfEffect,1000,"eyes",0)
    {
      transform(vector3(0,1,0));
    };
    facetotarget(0,100);
    charge(200,10,1,vector3(-1,0,-1),0);
    damage(0);
    addstate("sleep");
  };
  section(100)
  {
    removestate("sleep");
    animation("Stand");
    enablemoveagent(true);
  };
  onstop
  {
    removestate("sleep");
    animation("Stand");
    enablemoveagent(true);
  };
};